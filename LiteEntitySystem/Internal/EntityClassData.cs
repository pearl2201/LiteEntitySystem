using System;
using System.Collections.Generic;
using System.Reflection;

namespace LiteEntitySystem.Internal
{
    internal struct SyncableFieldInfo
    {
        public readonly Type OwnerType;
        public readonly string FieldName;
        public readonly SyncFlags Flags;
        public ushort RPCOffset;
        public ObjectFieldGetter<SyncableField> Accessor;

        public SyncableFieldInfo(Type ownerType, string fieldName, SyncFlags executeFlags)
        {
            OwnerType = ownerType;
            FieldName = fieldName;
            Flags = executeFlags;
            RPCOffset = ushort.MaxValue;
            Accessor = null;
        }
    }
    
    internal readonly struct RpcFieldInfo
    {
        public readonly ObjectFieldGetter<SyncableField> SyncableAccessor;
        public readonly MethodCallDelegate Method;

        public RpcFieldInfo(MethodCallDelegate method)
        {
            SyncableAccessor = null;
            Method = method;
        }
        
        public RpcFieldInfo(ObjectFieldGetter<SyncableField> syncableAccessor, MethodCallDelegate method)
        {
            SyncableAccessor = syncableAccessor;
            Method = method;
        }
    }

    internal struct BaseTypeInfo
    {
        public readonly Type Type;
        public readonly bool IsSingleton;
        public ushort Id;

        public BaseTypeInfo(Type type)
        {
            Type = type;
            IsSingleton = type.IsSubclassOf(EntityClassData.SingletonEntityType);
            Id = ushort.MaxValue;
        }
    }
    
    internal struct EntityClassData
    {
        public readonly string ClassEnumName;
        
        public readonly ushort ClassId;
        public readonly ushort FilterId;
        public readonly bool IsSingleton;
        public readonly int FieldsCount;
        public readonly int FieldsFlagsSize;
        public readonly int FixedFieldsSize;
        public readonly int PredictedSize;
        public readonly EntityFieldInfo[] Fields;
        public readonly SyncableFieldInfo[] SyncableFields;
        public readonly SyncableFieldInfo[] SyncableFieldsCustomRollback;
        public readonly int InterpolatedCount;
        public readonly EntityFieldInfo[] LagCompensatedFields;
        public readonly int LagCompensatedSize;
        public readonly int LagCompensatedCount;
        public readonly EntityFlags Flags;
        public readonly EntityConstructor<InternalEntity> EntityConstructor;
        public readonly BaseTypeInfo[] BaseTypes;
        public RpcFieldInfo[] RemoteCallsClient;
        public readonly Type Type;

        private readonly int[] _ownedRollbackFields;
        private readonly int[] _remoteRollbackFields;
        
        private static readonly Type InternalEntityType = typeof(InternalEntity);
        internal static readonly Type SingletonEntityType = typeof(SingletonEntityLogic);
        private static readonly Type SyncableFieldType = typeof(SyncableField);
        private static readonly Type EntityLogicType = typeof(EntityLogic);
        
        private readonly Queue<byte[]> _dataCache;
        private readonly int _dataCacheSize;
        private readonly int _maxHistoryCount;
        private readonly int _historyStart;
        
        public Span<byte> GetLastServerData(InternalEntity e) => new (e.IOBuffer, 0, PredictedSize);

        public int[] GetRollbackFields(bool isOwned) =>
            isOwned ? _ownedRollbackFields : _remoteRollbackFields;
        
        //here Offset used because SyncableFields doesn't support LagCompensated fields
        public unsafe void WriteHistory(EntityLogic e, ushort tick)
        {
            int historyOffset = ((tick % _maxHistoryCount)+1)*LagCompensatedSize;
            fixed (byte* history = &e.IOBuffer[_historyStart])
            {
                for (int i = 0; i < LagCompensatedCount; i++)
                {
                    ref var field = ref LagCompensatedFields[i];
                    field.TypeProcessor.WriteTo(e, field.ValueAccessor, history + historyOffset);
                    historyOffset += field.IntSize;
                }
            }
        }

        public unsafe void LoadHistroy(NetPlayer player, EntityLogic e)
        {
            int historyAOffset = ((player.StateATick % _maxHistoryCount)+1)*LagCompensatedSize;
            int historyBOffset = ((player.StateBTick % _maxHistoryCount)+1)*LagCompensatedSize;
            int historyCurrent = 0;
            fixed (byte* history = &e.IOBuffer[_historyStart])
            {
                for (int i = 0; i < LagCompensatedCount; i++)
                {
                    ref var field = ref LagCompensatedFields[i];
                    field.TypeProcessor.LoadHistory(
                        e, 
                        field.ValueAccessor,
                        history + historyCurrent,
                        history + historyAOffset,
                        history + historyBOffset,
                        player.LerpTime);
                    historyAOffset += field.IntSize;
                    historyBOffset += field.IntSize;
                    historyCurrent += field.IntSize;
                }
            }
        }

        public unsafe void UndoHistory(EntityLogic e)
        {
            int historyOffset = 0;
            fixed (byte* history = &e.IOBuffer[_historyStart])
            {
                for (int i = 0; i < LagCompensatedCount; i++)
                {
                    ref var field = ref LagCompensatedFields[i];
                    field.TypeProcessor.SetFrom(e, field.ValueAccessor, history + historyOffset);
                    historyOffset += field.IntSize;
                }
            }
        }
        
        public byte[] AllocateDataCache()
        {
            if (_dataCache.Count > 0)
            {
                byte[] data = _dataCache.Dequeue();
                Array.Clear(data, 0, data.Length);
                return data;
            }
            return new byte[_dataCacheSize];
        }

        public void ReleaseDataCache(InternalEntity entity)
        {
            if (entity.IOBuffer != null && entity.IOBuffer.Length == _dataCacheSize)
            {
                _dataCache.Enqueue(entity.IOBuffer);
                Array.Clear(entity.IOBuffer, 0, _dataCacheSize);
                entity.IOBuffer = null;
            }
        }

        public EntityClassData(EntityManager entityManager, ushort filterId, Type entType, RegisteredTypeInfo typeInfo)
        {
            _dataCache = new Queue<byte[]>();
            _accessorsResolved = false;
            PredictedSize = 0;
            FixedFieldsSize = 0;
            LagCompensatedSize = 0;
            InterpolatedCount = 0;
            RemoteCallsClient = null;
            ClassId = typeInfo.ClassId;
            ClassEnumName = typeInfo.ClassName;
            Flags = 0;
            EntityConstructor = typeInfo.Constructor;
            IsSingleton = entType.IsSubclassOf(SingletonEntityType);
            FilterId = filterId;

            var tempBaseTypes = Utils.GetBaseTypes(entType, InternalEntityType, false, true);
            BaseTypes = new BaseTypeInfo[tempBaseTypes.Count];
            for (int i = 0; i < BaseTypes.Length; i++)
                BaseTypes[i] = new BaseTypeInfo(tempBaseTypes.Pop());
            
            var fields = new List<EntityFieldInfo>();
            var syncableFields = new List<SyncableFieldInfo>();
            var syncableFieldsWithCustomRollback = new List<SyncableFieldInfo>();
            var lagCompensatedFields = new List<EntityFieldInfo>();
            var ownedRollbackFields = new List<int>();
            var remoteRollbackFields = new List<int>();
            
            var allTypesStack = Utils.GetBaseTypes(entType, InternalEntityType, true, true);
            while(allTypesStack.Count > 0)
            {
                var baseType = allTypesStack.Pop();
                
                var setFlagsAttribute = baseType.GetCustomAttribute<EntityFlagsAttribute>();
                Flags |= setFlagsAttribute != null ? setFlagsAttribute.Flags : 0;
                
                //cache fields
                foreach (var field in Utils.GetProcessedFields(baseType))
                {
                    var ft = field.FieldType;
                    if(Utils.IsRemoteCallType(ft) && !field.IsStatic)
                        throw new Exception($"RemoteCalls should be static! (Class: {entType} Field: {field.Name})");
                    
                    if(field.IsStatic)
                        continue;
                    
                    var syncVarFlags = field.GetCustomAttribute<SyncVarFlags>() ?? baseType.GetCustomAttribute<SyncVarFlags>();
                    var syncFlags = syncVarFlags?.Flags ?? SyncFlags.None;
                    
                    //syncvars
                    if (ft.IsGenericType && !ft.IsArray && ft.GetGenericTypeDefinition() == typeof(SyncVar<>))
                    {
                        ft = ft.GetGenericArguments()[0];
                        var valueType = ft;
                        bool isEnum = valueType.IsEnum;
                        ValueTypeProcessor valueTypeProcessor = null;
                        int fieldSize;
                        if (isEnum)
                        {
                            //Enum backed fields register their processor from generated code during accessor resolution
                            fieldSize = Utils.GetEnumSize(valueType);
                        }
                        else
                        {
                            if (!ValueTypeProcessor.Registered.TryGetValue(valueType, out valueTypeProcessor))
                            {
                                Logger.LogError($"Unregistered field type: {valueType}");
                                continue;
                            }
                            fieldSize = valueTypeProcessor.Size;
                        }
                        if (syncFlags.HasFlagFast(SyncFlags.Interpolated) && !isEnum)
                        {
                            InterpolatedCount++;
                        }
                        var fieldInfo = new EntityFieldInfo($"{baseType.Name}-{field.Name}", baseType, field.Name, syncVarFlags?.Flags ?? SyncFlags.None);
                        fieldInfo.TypeProcessor = valueTypeProcessor;
                        fieldInfo.ValueType = valueType;
                        fieldInfo.DeferredTypeProcessor = isEnum;
                        fieldInfo.Size = (uint)fieldSize;
                        fieldInfo.IntSize = fieldSize;
                        if (syncFlags.HasFlagFast(SyncFlags.LagCompensated))
                        {
                            lagCompensatedFields.Add(fieldInfo);
                            LagCompensatedSize += fieldSize;
                        }

                        if (fieldInfo.IsPredicted)
                            PredictedSize += fieldSize;

                        fields.Add(fieldInfo);
                        FixedFieldsSize += fieldSize;
                    }
                    else if (ft.IsSubclassOf(SyncableFieldType))
                    {
                        if (!field.IsInitOnly)
                            throw new Exception($"Syncable fields should be readonly! (Class: {entType} Field: {field.Name})");
                        
                        syncableFields.Add(new SyncableFieldInfo(baseType, field.Name, syncFlags));
                        
                        //add custom rollbacked separately
                        if (ft.IsSubclassOf(typeof(SyncableFieldCustomRollback)))
                            syncableFieldsWithCustomRollback.Add(new SyncableFieldInfo(baseType, field.Name, syncFlags));
                        
                        var syncableFieldTypesWithBase = Utils.GetBaseTypes(ft, SyncableFieldType, true, true);
                        while(syncableFieldTypesWithBase.Count > 0)
                        {
                            var syncableType = syncableFieldTypesWithBase.Pop();
                            //syncable fields
                            foreach (var syncableField in Utils.GetProcessedFields(syncableType))
                            {
                                var syncableFieldType = syncableField.FieldType;
                                if(Utils.IsRemoteCallType(syncableFieldType) && !syncableField.IsStatic)
                                    throw new Exception($"RemoteCalls should be static! (Class: {syncableType} Field: {syncableField.Name})");
                                
                                if (!syncableFieldType.IsValueType || 
                                    !syncableFieldType.IsGenericType || 
                                    syncableFieldType.GetGenericTypeDefinition() != typeof(SyncVar<>) ||
                                    syncableField.IsStatic) 
                                    continue;

                                var syncValueType = syncableFieldType.GetGenericArguments()[0];
                                bool syncIsEnum = syncValueType.IsEnum;
                                ValueTypeProcessor valueTypeProcessor = null;
                                int syncFieldSize;
                                if (syncIsEnum)
                                {
                                    //Enum backed fields register their processor from generated code during accessor resolution
                                    syncFieldSize = Utils.GetEnumSize(syncValueType);
                                }
                                else
                                {
                                    if (!ValueTypeProcessor.Registered.TryGetValue(syncValueType, out valueTypeProcessor))
                                    {
                                        Logger.LogError($"Unregistered field type: {syncValueType}");
                                        continue;
                                    }
                                    syncFieldSize = valueTypeProcessor.Size;
                                }
                                
                                var mergedSyncFlags = (syncableField.GetCustomAttribute<SyncVarFlags>()?.Flags ?? SyncFlags.None) | (syncVarFlags?.Flags ?? SyncFlags.None);
                                if (mergedSyncFlags.HasFlagFast(SyncFlags.OnlyForOwner) &&
                                    mergedSyncFlags.HasFlagFast(SyncFlags.OnlyForOtherPlayers))
                                {
                                    Logger.LogWarning($"{SyncFlags.OnlyForOwner} and {SyncFlags.OnlyForOtherPlayers} flags can't be used together! Field: {syncableType} - {syncableField.Name}");
                                }
                                if (mergedSyncFlags.HasFlagFast(SyncFlags.AlwaysRollback) &&
                                    mergedSyncFlags.HasFlagFast(SyncFlags.NeverRollBack))
                                {
                                    Logger.LogWarning($"{SyncFlags.AlwaysRollback} and {SyncFlags.NeverRollBack} flags can't be used together! Field: {syncableType} - {syncableField.Name}");
                                }
                                
                                var fieldInfo = new EntityFieldInfo($"{baseType.Name}-{field.Name}:{syncableField.Name}", baseType, field.Name, syncableType, syncableField.Name, mergedSyncFlags);
                                fieldInfo.TypeProcessor = valueTypeProcessor;
                                fieldInfo.ValueType = syncValueType;
                                fieldInfo.DeferredTypeProcessor = syncIsEnum;
                                fieldInfo.Size = (uint)syncFieldSize;
                                fieldInfo.IntSize = syncFieldSize;
                                fields.Add(fieldInfo);
                                FixedFieldsSize += fieldInfo.IntSize;
                                if (fieldInfo.IsPredicted)
                                    PredictedSize += fieldInfo.IntSize;
                            }
                        }
                    }
                }
            }
            
            //sort by placing interpolated first
            // List.Sort is UNSTABLE, so a comparator that only ranks the interpolated flag leaves the order of
            // equal-ranked fields implementation-defined — which can differ across .NET runtimes and break the
            // cross-peer field layout. Tie-break by the (unique) field Name with Ordinal comparison to make the
            // order a total, environment-independent ordering.
            fields.Sort((a, b) =>
            {
                int wa = a.Flags.HasFlagFast(SyncFlags.Interpolated) ? 1 : 0;
                int wb = b.Flags.HasFlagFast(SyncFlags.Interpolated) ? 1 : 0;
                if (wa != wb) return wb - wa;
                return string.CompareOrdinal(a.Name, b.Name);
            });
            Fields = fields.ToArray();
            SyncableFields = syncableFields.ToArray();
            SyncableFieldsCustomRollback = syncableFieldsWithCustomRollback.ToArray();
            FieldsCount = Fields.Length;
            FieldsFlagsSize = (FieldsCount-1) / 8 + 1;
            LagCompensatedFields = lagCompensatedFields.ToArray();
            LagCompensatedCount = LagCompensatedFields.Length;

            int fixedOffset = 0;
            int predictedOffset = 0;
            for (int i = 0; i < Fields.Length; i++)
            {
                ref var field = ref Fields[i];
                field.FixedOffset = fixedOffset;
                fixedOffset += field.IntSize;
                if (field.IsPredicted)
                {
                    field.PredictedOffset = predictedOffset;
                    predictedOffset += field.IntSize;
                    
                    //singletons can't be owned
                    if (!IsSingleton)
                        ownedRollbackFields.Add(i);
                    
                    if(field.Flags.HasFlagFast(SyncFlags.AlwaysRollback))
                        remoteRollbackFields.Add(i);
                }
                else
                {
                    field.PredictedOffset = -1;
                }
            }
            
            //cache rollbackFields
            _ownedRollbackFields = ownedRollbackFields.ToArray();
            _remoteRollbackFields = remoteRollbackFields.ToArray();
            
            _maxHistoryCount = (byte)entityManager.MaxHistorySize;
            int historySize = entType.IsSubclassOf(EntityLogicType) ? (_maxHistoryCount + 1) * LagCompensatedSize : 0;
            if (entityManager.IsServer)
            {
                _dataCacheSize = historySize + StateSerializer.HeaderSize + FixedFieldsSize;
                _historyStart = StateSerializer.HeaderSize + FixedFieldsSize;
            }
            else
            {
                _dataCacheSize = PredictedSize + historySize;
                _historyStart = PredictedSize;
            }

            Type = entType;
        }

        private bool _accessorsResolved;

        private static readonly Dictionary<Type, SyncVarAccessorMap> SyncableAccessorCache = new ();

        /// <summary>
        /// Resolves generated field accessors. Executed once per class - on the first constructed entity of that class -
        /// because accessors are emitted per declaring type by the LiteEntitySystem source generator.
        /// </summary>
        public void ResolveAccessors(InternalEntity entity)
        {
            if (_accessorsResolved)
                return;
            _accessorsResolved = true;

            var map = new SyncVarAccessorMap();
            entity.RegisterSyncVarAccessors(map);

            for (int i = 0; i < Fields.Length; i++)
            {
                ref var field = ref Fields[i];
                if (!map.TryGet(field.OwnerType, field.FieldName, out var ownerAccessor))
                {
                    Logger.LogError($"Missing generated accessor for field '{field.Name}'. " +
                                    "Make sure the LiteEntitySystem source generator is enabled for this assembly.");
                    continue;
                }

                if (field.FieldType == FieldType.SyncVar)
                {
                    field.ValueAccessor = ownerAccessor;
                    ResolveDeferredTypeProcessor(ref field);
                    continue;
                }

                field.TargetAccessor = (ObjectFieldGetter<SyncableField>)ownerAccessor;
                var syncableMap = GetSyncableAccessors(field.TargetAccessor(entity));
                if (!syncableMap.TryGet(field.InnerOwnerType, field.InnerFieldName, out var innerAccessor))
                {
                    Logger.LogError($"Missing generated accessor for field '{field.Name}'. " +
                                    "Make sure the LiteEntitySystem source generator is enabled for this assembly.");
                    continue;
                }
                field.ValueAccessor = innerAccessor;
                ResolveDeferredTypeProcessor(ref field);
            }

            //Lag compensated fields are copies of the main field infos - propagate resolved accessors
            for (int i = 0; i < LagCompensatedFields.Length; i++)
            {
                ref var lagField = ref LagCompensatedFields[i];
                for (int j = 0; j < Fields.Length; j++)
                {
                    if (Fields[j].Name != lagField.Name)
                        continue;
                    lagField.ValueAccessor = Fields[j].ValueAccessor;
                    lagField.TargetAccessor = Fields[j].TargetAccessor;
                    lagField.TypeProcessor = Fields[j].TypeProcessor;
                    break;
                }
            }

            for (int i = 0; i < SyncableFields.Length; i++)
                ResolveSyncableFieldInfo(ref SyncableFields[i], map);
            for (int i = 0; i < SyncableFieldsCustomRollback.Length; i++)
                ResolveSyncableFieldInfo(ref SyncableFieldsCustomRollback[i], map);
        }

        private static void ResolveDeferredTypeProcessor(ref EntityFieldInfo field)
        {
            if (!field.DeferredTypeProcessor)
                return;
            if (ValueTypeProcessor.Registered.TryGetValue(field.ValueType, out var processor))
                field.TypeProcessor = processor;
            else
                Logger.LogError($"Unregistered enum field type: {field.ValueType}. " +
                                "Make sure the LiteEntitySystem source generator is enabled for this assembly.");
        }

        private static void ResolveSyncableFieldInfo(ref SyncableFieldInfo info, SyncVarAccessorMap map)
        {
            if (map.TryGet(info.OwnerType, info.FieldName, out var accessor))
                info.Accessor = (ObjectFieldGetter<SyncableField>)accessor;
            else
                Logger.LogError($"Missing generated accessor for SyncableField '{info.OwnerType}.{info.FieldName}'. " +
                                "Make sure the LiteEntitySystem source generator is enabled for this assembly.");
        }

        private static SyncVarAccessorMap GetSyncableAccessors(SyncableField syncableField)
        {
            var syncableType = syncableField.GetType();
            if (SyncableAccessorCache.TryGetValue(syncableType, out var map))
                return map;
            map = new SyncVarAccessorMap();
            syncableField.RegisterSyncVarAccessors(map);
            SyncableAccessorCache[syncableType] = map;
            return map;
        }

        public void PrepareBaseTypes(Dictionary<Type, ushort> registeredTypeIds, ref ushort singletonCount, ref ushort filterCount)
        {
            if (Type == null)
                return;
            for (int i = 0; i < BaseTypes.Length; i++)
            {
                ref var baseTypeInfo = ref BaseTypes[i];
                if (!registeredTypeIds.TryGetValue(baseTypeInfo.Type, out baseTypeInfo.Id))
                {
                    baseTypeInfo.Id = baseTypeInfo.IsSingleton
                        ? singletonCount++
                        : filterCount++;
                    registeredTypeIds.Add(baseTypeInfo.Type, baseTypeInfo.Id);
                    //Logger.Log($"Register Base {i} type of {ClassId} - type: {_baseTypes[i]}, id: {BaseIds[i]}");
                }
            }
        }
    }

    // ReSharper disable once UnusedTypeParameter
    internal static class EntityClassInfo<T>
    {
        // ReSharper disable once StaticMemberInGenericType
        internal static ushort ClassId;
    }
}