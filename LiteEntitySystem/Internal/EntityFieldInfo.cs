using System;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    internal enum FieldType
    {
        SyncVar,
        SyncableSyncVar
    }

    internal struct EntityFieldInfo
    {
        public readonly string Name; //used for debug
        public readonly FieldType FieldType;
        public readonly SyncFlags Flags;
        public readonly bool IsPredicted;

        // --- accessor resolution metadata (replaces runtime field offsets) ---

        /// <summary>
        /// Type that declares the target field. For <see cref="FieldType.SyncVar"/> it's the type declaring
        /// the SyncVar&lt;T&gt; field. For <see cref="FieldType.SyncableSyncVar"/> it's the entity type declaring
        /// the SyncableField field.
        /// </summary>
        public readonly Type OwnerType;

        /// <summary>
        /// Name of the field on <see cref="OwnerType"/>.
        /// </summary>
        public readonly string FieldName;

        /// <summary>
        /// For <see cref="FieldType.SyncableSyncVar"/> - type declaring the SyncVar&lt;T&gt; inside the SyncableField.
        /// </summary>
        public readonly Type InnerOwnerType;

        /// <summary>
        /// For <see cref="FieldType.SyncableSyncVar"/> - name of the SyncVar&lt;T&gt; field inside the SyncableField.
        /// </summary>
        public readonly string InnerFieldName;

        // --- resolved data ---

        /// <summary>
        /// Value processor. For enum backed fields it is resolved later (see <see cref="DeferredTypeProcessor"/>),
        /// because the generated code has to register it first.
        /// </summary>
        public ValueTypeProcessor TypeProcessor;

        /// <summary>
        /// Value type of the field (the SyncVar&lt;T&gt; type argument).
        /// </summary>
        public Type ValueType;

        /// <summary>
        /// True when <see cref="TypeProcessor"/> must be looked up after generated code ran (enum backed fields).
        /// </summary>
        public bool DeferredTypeProcessor;

        public uint Size;
        public int IntSize;

        /// <summary>
        /// Generated <see cref="SyncVarRefGetter{T}"/> that returns the SyncVar&lt;T&gt; of the target object.
        /// </summary>
        public Delegate ValueAccessor;

        /// <summary>
        /// Generated <see cref="ObjectFieldGetter{T}"/> that returns the SyncableField instance
        /// (only set for <see cref="FieldType.SyncableSyncVar"/>).
        /// </summary>
        public ObjectFieldGetter<SyncableField> TargetAccessor;

        public MethodCallDelegate OnSync;
        public BindOnChangeFlags OnSyncFlags;
        public int FixedOffset;
        public int PredictedOffset;

        //for value type
        public EntityFieldInfo(string name, Type ownerType, string fieldName, SyncFlags flags) : 
            this(name, ownerType, fieldName, null, null, flags, FieldType.SyncVar)
        {

        }

        //For syncable syncvar
        public EntityFieldInfo(string name, Type ownerType, string fieldName, Type innerOwnerType, string innerFieldName, SyncFlags flags) :
            this(name, ownerType, fieldName, innerOwnerType, innerFieldName, flags, FieldType.SyncableSyncVar)
        {

        }
        
        private EntityFieldInfo(
            string name,
            Type ownerType,
            string fieldName,
            Type innerOwnerType,
            string innerFieldName,
            SyncFlags flags,
            FieldType fieldType)
        {
            OnSyncFlags = 0;
            Name = name;
            OwnerType = ownerType;
            FieldName = fieldName;
            InnerOwnerType = innerOwnerType;
            InnerFieldName = innerFieldName;
            FieldType = fieldType;
            Flags = flags;
            IsPredicted = Flags.HasFlagFast(SyncFlags.AlwaysRollback) ||
                          (!Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers) &&
                           !Flags.HasFlagFast(SyncFlags.NeverRollBack));
            TypeProcessor = null;
            ValueType = null;
            DeferredTypeProcessor = false;
            Size = 0;
            IntSize = 0;
            ValueAccessor = null;
            TargetAccessor = null;
            FixedOffset = 0;
            PredictedOffset = 0;
            OnSync = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InternalBaseClass GetTargetObjectAndAccessor(InternalEntity entity, out Delegate accessor)
        {
            accessor = ValueAccessor;
            if (FieldType == FieldType.SyncableSyncVar)
                return TargetAccessor(entity);
            return entity;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public InternalBaseClass GetTargetObject(InternalEntity entity) =>
            FieldType == FieldType.SyncableSyncVar
                ? TargetAccessor(entity)
                : entity;
    }
}