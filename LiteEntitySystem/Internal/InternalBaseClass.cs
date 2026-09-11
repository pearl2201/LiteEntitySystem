namespace LiteEntitySystem.Internal
{
    /// <summary>
    /// Base class for SyncableFields and Entities
    /// </summary>
    public abstract partial class InternalBaseClass
    {
        /// <summary>
        /// Method for executing RPCs containing initial sync data that need to be sent after entity creation
        /// to existing players or when new player connected
        /// </summary>
        protected internal virtual void OnSyncRequested()
        {
            
        }

        /// <summary>
        /// Registers generated accessors for all fields declared by this type that are part of the sync system
        /// (<see cref="SyncVar{T}"/> fields and <see cref="SyncableField"/> fields).
        /// </summary>
        /// <remarks>
        /// Overrides are emitted by the LiteEntitySystem source generator. Implementations must first call
        /// <c>base.RegisterSyncVarAccessors(map)</c> so that fields declared by base types are also registered.
        /// </remarks>
        /// <param name="map">Map that receives the generated accessors</param>
        protected internal virtual void RegisterSyncVarAccessors(SyncVarAccessorMap map)
        {
            
        }
    }
}