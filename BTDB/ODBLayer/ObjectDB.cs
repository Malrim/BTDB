﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BTDB.Buffer;
using BTDB.Encrypted;
using BTDB.FieldHandler;
using BTDB.KVDBLayer;
using BTDB.StreamLayer;

namespace BTDB.ODBLayer
{
    public class ObjectDB : IObjectDB
    {
        IKeyValueDB _keyValueDB;
        internal ISymmetricCipher SymmetricCipher;
        IType2NameRegistry _type2Name;
        IPolymorphicTypesRegistry _polymorphicTypesRegistry;
        TablesInfo _tablesInfo;
        RelationsInfo _relationsInfo;
        internal Dictionary<Type, Func<IObjectDBTransaction, IRelation>> RelationFactories = new Dictionary<Type, Func<IObjectDBTransaction, IRelation>>();
        bool _dispose;
        internal static readonly byte[] TableNamesPrefix = { 0, 0 }; // Index Table => Name
        const int TableNamesPrefixLen = 2;
        internal static readonly byte[] TableVersionsPrefix = { 0, 1 }; // Index Table, Version => TableVersionInfo
        const uint TableVersionsPrefixLen = 2;
        internal static readonly byte[] TableSingletonsPrefix = { 0, 2 }; // Index Table => singleton oid
        const uint TableSingletonsPrefixLen = 2;
        internal static readonly byte[] LastDictIdKey = { 0, 3 }; //  => Last Dictionary Index - only for backward compatibility newly stored in Ulong[1]
        internal static readonly byte[] RelationNamesPrefix = { 0, 4 }; // Name => Index Relation
        const int RelationNamesPrefixLen = 2;
        internal static readonly byte[] RelationVersionsPrefix = { 0, 5 }; // Index Relation, version number => metadata
        internal static readonly byte[] AllObjectsPrefix = { 1 }; // oid => Index Table, version number, Value
        internal const int AllObjectsPrefixLen = 1;
        internal static readonly byte[] AllDictionariesPrefix = { 2 }; // Index Dictionary, Key => Value
        internal static readonly byte[] AllRelationsPKPrefix = { 3 }; // Index Relation, Primary Key => version number, Value (without primary key)
        internal static readonly byte[] AllRelationsSKPrefix = { 4 }; // Index Relation, Secondary Key Index, Secondary Key, primary key fields not present in secondary key => {}
        internal const byte AllObjectsPrefixByte = 1;
        internal const byte AllDictionariesPrefixByte = 2;
        internal const byte AllRelationsPKPrefixByte = 3;
        internal const byte AllRelationsSKPrefixByte = 4;
        ITableInfoResolver _tableInfoResolver;
        IRelationInfoResolver _relationsInfoResolver;
        long _lastObjId;
        ulong _lastDictId;

        // ReSharper disable once NotNullMemberIsNotInitialized
        public ObjectDB()
        {
            FieldHandlerFactory = new DefaultODBFieldHandlerFactory(this);
            TypeConvertorGenerator = DefaultTypeConvertorGenerator.Instance;
        }

        /// Use only for tests of upgrade compatibility in same process, never use in production code
        public static void ResetAllMetadataCaches()
        {
            RelationBuilder.Reset();
            ODBDictionaryConfiguration.Reset();
        }

        internal ulong LastDictId => _lastDictId;

        internal IType2NameRegistry Type2NameRegistry => _type2Name;

        internal bool AutoRegisterTypes { get; private set; }

        public DBOptions ActualOptions { get; private set; }

        internal TablesInfo TablesInfo => _tablesInfo;

        internal RelationsInfo RelationsInfo => _relationsInfo;
        internal IRelationInfoResolver RelationInfoResolver => _relationsInfoResolver;

        public void Open(IKeyValueDB keyValueDB, bool dispose)
        {
            Open(keyValueDB, dispose, new DBOptions());
        }

        public void Open(IKeyValueDB keyValueDB, bool dispose, DBOptions options)
        {
            _keyValueDB = keyValueDB ?? throw new ArgumentNullException(nameof(keyValueDB));
            _dispose = dispose;
            _type2Name = options.CustomType2NameRegistry ?? new Type2NameRegistry();
            _polymorphicTypesRegistry = new PolymorphicTypesRegistry();
            AutoRegisterTypes = options.AutoRegisterType;
            ActualOptions = options;
            SymmetricCipher = options.SymmetricCipher ?? new InvalidSymmetricCipher();

            _tableInfoResolver = new TableInfoResolver(keyValueDB, this);
            _tablesInfo = new TablesInfo(_tableInfoResolver);
            _relationsInfoResolver = new RelationInfoResolver(this);
            _relationsInfo = new RelationsInfo(_relationsInfoResolver);

            using var tr = _keyValueDB.StartTransaction();
            _lastObjId = (long)tr.GetUlong(0);
            _lastDictId = tr.GetUlong(1);
            if (_lastObjId == 0)
            {
                if (tr.FindLastKey(AllObjectsPrefix))
                {
                    _lastObjId = (long)PackUnpack.UnpackVUInt(tr.GetKeyAsReadOnlySpan().Slice(AllObjectsPrefixLen));
                }
            }
            _tablesInfo.LoadTables(LoadTablesEnum(tr));
            _relationsInfo.LoadRelations(LoadRelationNamesEnum(tr));
            if (_lastDictId == 0)
            {
                if (tr.FindExactKey(LastDictIdKey))
                {
                    _lastDictId = PackUnpack.UnpackVUInt(tr.GetValueAsReadOnlySpan());
                }
            }
        }

        internal void CommitLastObjIdAndDictId(ulong newLastDictId, IKeyValueDBTransaction tr)
        {
            tr.SetUlong(0, (ulong)_lastObjId);
            if (_lastDictId != newLastDictId)
            {
                tr.SetUlong(1, newLastDictId);
                _lastDictId = newLastDictId;
            }
        }

        internal static IEnumerable<KeyValuePair<uint, string>> LoadTablesEnum(IKeyValueDBTransaction tr)
        {
            tr.InvalidateCurrentKey();
            while (tr.FindNextKey(TableNamesPrefix))
            {
                yield return new KeyValuePair<uint, string>(new SpanReader(tr.GetKeyAsReadOnlySpan().Slice(TableNamesPrefixLen)).ReadVUInt32(), new SpanReader(tr.GetValueAsReadOnlySpan()).ReadString());
            }
        }

        internal static IEnumerable<KeyValuePair<uint, string>> LoadRelationNamesEnum(IKeyValueDBTransaction tr)
        {
            tr.InvalidateCurrentKey();
            while (tr.FindNextKey(RelationNamesPrefix))
            {
                yield return new KeyValuePair<uint, string>(new SpanReader(tr.GetValueAsReadOnlySpan()).ReadVUInt32(), new SpanReader(tr.GetKeyAsReadOnlySpan().Slice(RelationNamesPrefixLen)).ReadString());
            }
        }

        public IObjectDBTransaction StartTransaction()
        {
            return new ObjectDBTransaction(this, _keyValueDB.StartTransaction(), false);
        }

        public IObjectDBTransaction StartReadOnlyTransaction()
        {
            return new ObjectDBTransaction(this, _keyValueDB.StartReadOnlyTransaction(), true);
        }

        public ValueTask<IObjectDBTransaction> StartWritingTransaction()
        {
            var tr = _keyValueDB.StartWritingTransaction();
            if(tr.IsCompletedSuccessfully)
                return new ValueTask<IObjectDBTransaction>(new ObjectDBTransaction(this, tr.Result, false));

            return new ValueTask<IObjectDBTransaction>(tr.AsTask()
                .ContinueWith<IObjectDBTransaction>(t => new ObjectDBTransaction(this, t.Result, false),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        }

        public string RegisterType(Type type)
        {
            return RegisterType(type, true);
        }

        public string RegisterType(Type type, string withName)
        {
            return RegisterType(type, withName, true);
        }

        internal string RegisterType(Type type, bool manualRegistration)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            var name = Type2NameRegistry.FindNameByType(type);
            if (name != null) return name;
            name = type.Name;
            if (type.IsInterface && name.StartsWith("I", StringComparison.Ordinal)) name = name.Substring(1);
            return RegisterType(type, name, manualRegistration);
        }

        internal string RegisterType(Type type, string asName, bool manualRegistration)
        {
            if (manualRegistration)
                _polymorphicTypesRegistry.RegisterPolymorphicType(type);
            return Type2NameRegistry.RegisterType(type, asName);
        }

        public IEnumerable<Type> GetPolymorphicTypes(Type baseType)
        {
            return _polymorphicTypesRegistry.GetPolymorphicTypes(baseType);
        }

        public Type? TypeByName(string name)
        {
            return Type2NameRegistry.FindTypeByName(name);
        }

        public IObjectDBLogger Logger { get; set; }

        public ISymmetricCipher GetSymmetricCipher() => SymmetricCipher;

        public void RegisterCustomRelation(Type type, Func<IObjectDBTransaction, IRelation> factory)
        {
            while (true)
            {
                var currentRelationFactories = Volatile.Read(ref RelationFactories);
                if (currentRelationFactories!.ContainsKey(type)) return;
                var newRelationFactories =
                    new Dictionary<Type, Func<IObjectDBTransaction, IRelation>>(currentRelationFactories)
                    {
                        {type, factory}
                    };
                if (Interlocked.CompareExchange(ref RelationFactories, newRelationFactories,
                    currentRelationFactories) == currentRelationFactories)
                    return;
            }
        }

        public bool AllowAutoRegistrationOfRelations { get; set; } = true;

        public void Dispose()
        {
            if (_dispose)
            {
                _keyValueDB.Dispose();
                _dispose = false;
            }
        }

        class TableInfoResolver : ITableInfoResolver
        {
            readonly IKeyValueDB _keyValueDB;
            readonly ObjectDB _objectDB;

            internal TableInfoResolver(IKeyValueDB keyValueDB, ObjectDB objectDB)
            {
                _keyValueDB = keyValueDB;
                _objectDB = objectDB;
            }

            uint ITableInfoResolver.GetLastPersistedVersion(uint id)
            {
                using var tr = _keyValueDB.StartTransaction();
                var key = TableInfo.BuildKeyForTableVersions(id, uint.MaxValue);
                var ofs = PackUnpack.LengthVUInt(id);
                if (tr.Find(key, TableVersionsPrefixLen + ofs) == FindResult.NotFound)
                    return 0;
                var key2 = tr.GetKeyAsReadOnlySpan().Slice((int)(TableVersionsPrefixLen + ofs));
                return checked((uint)PackUnpack.UnpackVUInt(key2));
            }

            TableVersionInfo ITableInfoResolver.LoadTableVersionInfo(uint id, uint version, string tableName)
            {
                using var tr = _keyValueDB.StartReadOnlyTransaction();
                var key = TableInfo.BuildKeyForTableVersions(id, version);
                if (!tr.FindExactKey(key))
                    throw new BTDBException($"Missing TableVersionInfo Id:{id} Version:{version}");
                var reader = new SpanReader(tr.GetValueAsReadOnlySpan());
                return TableVersionInfo.Load(ref reader, _objectDB.FieldHandlerFactory, tableName);
            }

            public long GetSingletonOid(uint id)
            {
                using var tr = _keyValueDB.StartTransaction();
                var len = PackUnpack.LengthVUInt(id);
                Span<byte> key = stackalloc byte[(int)(TableSingletonsPrefixLen + len)];
                TableSingletonsPrefix.CopyTo(key);
                PackUnpack.UnsafePackVUInt( ref Unsafe.AddByteOffset(ref MemoryMarshal.GetReference(key), (IntPtr)TableSingletonsPrefixLen), id, len);
                if (tr.FindExactKey(key))
                {
                    return (long)PackUnpack.UnpackVUInt(tr.GetValueAsReadOnlySpan());
                }
                return 0;
            }

            public ulong AllocateNewOid()
            {
                return _objectDB.AllocateNewOid();
            }

            public IFieldHandlerFactory FieldHandlerFactory => _objectDB.FieldHandlerFactory;

            public ITypeConvertorGenerator TypeConvertorGenerator => _objectDB.TypeConvertorGenerator;
        }

        public ITypeConvertorGenerator TypeConvertorGenerator { get; set; }
        public IFieldHandlerFactory FieldHandlerFactory { get; set; }

        internal ulong AllocateNewOid()
        {
            return (ulong)Interlocked.Increment(ref _lastObjId);
        }

        internal ulong GetLastAllocatedOid()
        {
            return (ulong)Interlocked.Read(ref _lastObjId);
        }
    }
}
