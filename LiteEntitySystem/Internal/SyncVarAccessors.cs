using System;
using System.Collections.Generic;

namespace LiteEntitySystem.Internal
{
    /// <summary>
    /// Returns a reference to a <see cref="SyncVar{T}"/> field of a specific object instance.
    /// Implementations are emitted by the LiteEntitySystem source generator.
    /// </summary>
    /// <remarks>
    /// This replaces the old (IL/offset based) <c>RefMagic</c> field access, which relied on runtime
    /// field metadata that is not available in IL2CPP/WebGL builds.
    /// </remarks>
    public delegate ref SyncVar<T> SyncVarRefGetter<T>(InternalBaseClass obj) where T : unmanaged;

    /// <summary>
    /// Returns the value of an object reference field (used for <see cref="SyncableField"/> members) of a specific instance.
    /// Implementations are emitted by the LiteEntitySystem source generator.
    /// </summary>
    public delegate TField ObjectFieldGetter<out TField>(InternalBaseClass obj);

    /// <summary>
    /// Collects generated field accessors, keyed by the type that declares the field and its name.
    /// </summary>
    public sealed class SyncVarAccessorMap
    {
        private readonly Dictionary<(Type DeclaringType, string Name), Delegate> _accessors = new ();

        /// <summary>
        /// Register an accessor for a field declared in <paramref name="declaringType"/>.
        /// </summary>
        public void Add(Type declaringType, string fieldName, Delegate accessor) =>
            _accessors[(declaringType, fieldName)] = accessor;

        /// <summary>
        /// Tries to get a registered accessor for a field declared in <paramref name="declaringType"/>.
        /// </summary>
        public bool TryGet(Type declaringType, string fieldName, out Delegate accessor) =>
            _accessors.TryGetValue((declaringType, fieldName), out accessor);
    }
}
