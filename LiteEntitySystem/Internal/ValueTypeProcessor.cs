using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LiteEntitySystem.Internal
{
    public delegate T InterpolatorDelegateWithReturn<T>(T prev, T current, float t) where T : unmanaged;
    
    internal abstract unsafe class ValueTypeProcessor
    {
        public static readonly Dictionary<Type, ValueTypeProcessor> Registered = new ();
        
        internal readonly int Size;

        protected ValueTypeProcessor(int size) => Size = size;

        internal abstract void InitSyncVar(InternalBaseClass obj, Delegate accessor, InternalEntity entity, ushort fieldId);
        internal abstract void SetFrom(InternalBaseClass obj, Delegate accessor, byte* data);
        internal abstract bool SetFromAndSync(InternalBaseClass obj, Delegate accessor, byte* data, bool copyPrevValueIntoData);
        internal abstract void SetFromAndSync(InternalBaseClass obj, Delegate accessor, byte* data, MethodCallDelegate onSyncDelegate);
        internal abstract void SetInterpValue(InternalBaseClass obj, Delegate accessor, byte* data);
        internal abstract void SetInterpValueFromCurrentValue(InternalBaseClass obj, Delegate accessor);
        internal abstract void WriteTo(InternalBaseClass obj, Delegate accessor, byte* data);
        internal abstract void CopyFrom(InternalBaseClass toObj, InternalBaseClass fromObj, Delegate accessor);
        internal abstract void LoadHistory(InternalBaseClass obj, Delegate accessor, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime);
        internal abstract int GetHashCode(InternalBaseClass obj, Delegate accessor);
        internal abstract string ToString(InternalBaseClass obj, Delegate accessor);
    }

    internal unsafe class ValueTypeProcessor<T> : ValueTypeProcessor where T : unmanaged
    {
        public ValueTypeProcessor() : base(sizeof(T)) { }

        internal virtual T GetInterpolatedValue(T prev, T current, float t) => current;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref SyncVar<T> GetRef(InternalBaseClass obj, Delegate accessor) =>
            ref ((SyncVarRefGetter<T>)accessor)(obj);

        internal sealed override void InitSyncVar(InternalBaseClass obj, Delegate accessor, InternalEntity entity, ushort fieldId) =>
            GetRef(obj, accessor).Init(entity, fieldId);

        internal override void CopyFrom(InternalBaseClass toObj, InternalBaseClass fromObj, Delegate accessor) =>
            GetRef(toObj, accessor).SetDirect(GetRef(fromObj, accessor).Value);

        internal override void LoadHistory(InternalBaseClass obj, Delegate accessor, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            GetRef(obj, accessor).SetDirectAndStorePrev(*(T*)historyA, out *(T*)tempHistory);
        
        internal sealed override void SetFrom(InternalBaseClass obj, Delegate accessor, byte* data) =>
            GetRef(obj, accessor).SetDirect(*(T*)data);

        internal sealed override bool SetFromAndSync(InternalBaseClass obj, Delegate accessor, byte* data, bool copyPrevValueIntoData)
        {
            if(copyPrevValueIntoData)
            {
                return GetRef(obj, accessor).SetFromAndSync(ref *(T*)data);
            }
            else
            {
                var tempData = *(T*)data;
                return GetRef(obj, accessor).SetFromAndSync(ref tempData);
            }
        }

        internal sealed override void SetFromAndSync(InternalBaseClass obj, Delegate accessor, byte* data, MethodCallDelegate onSyncDelegate)
        {
            var tempData = *(T*)data;
            if(GetRef(obj, accessor).SetFromAndSync(ref tempData))
                onSyncDelegate(obj, new ReadOnlySpan<byte>(&tempData, Size));
        }

        internal sealed override void SetInterpValue(InternalBaseClass obj, Delegate accessor, byte* data) =>
            GetRef(obj, accessor).SetInterpValue(*(T*)data);
        
        internal sealed override void SetInterpValueFromCurrentValue(InternalBaseClass obj, Delegate accessor) =>
            GetRef(obj, accessor).SetInterpValueFromCurrent();

        internal sealed override void WriteTo(InternalBaseClass obj, Delegate accessor, byte* data) =>
            *(T*)data = GetRef(obj, accessor).Value;

        internal sealed override int GetHashCode(InternalBaseClass obj, Delegate accessor) =>
            GetRef(obj, accessor).GetHashCode();
        
        internal sealed override string ToString(InternalBaseClass obj, Delegate accessor) =>
            GetRef(obj, accessor).ToString();
    }

    internal class ValueTypeProcessorInt : ValueTypeProcessor<int>
    {
        internal override int GetInterpolatedValue(int prev, int current, float t) => Utils.Lerp(prev, current, t);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, Delegate accessor, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            GetRef(obj, accessor).SetDirectAndStorePrev(
                Utils.Lerp(*(int*)historyA, *(int*)historyB, lerpTime), out *(int*)tempHistory);
    }
    
    internal class ValueTypeProcessorLong : ValueTypeProcessor<long>
    {
        internal override long GetInterpolatedValue(long prev, long current, float t) => Utils.Lerp(prev, current, t);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, Delegate accessor, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            GetRef(obj, accessor).SetDirectAndStorePrev(
                Utils.Lerp(*(long*)historyA, *(long*)historyB, lerpTime), out *(long*)tempHistory);
    }

    internal class ValueTypeProcessorFloat : ValueTypeProcessor<float>
    {
        internal override float GetInterpolatedValue(float prev, float current, float t) => Utils.Lerp(prev, current, t);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, Delegate accessor, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            GetRef(obj, accessor).SetDirectAndStorePrev(
                Utils.Lerp(*(float*)historyA, *(float*)historyB, lerpTime), out *(float*)tempHistory);
    }
    
    internal class ValueTypeProcessorDouble : ValueTypeProcessor<double>
    {
        internal override double GetInterpolatedValue(double prev, double current, float t) => Utils.Lerp(prev, current, t);
        
        internal override unsafe void LoadHistory(InternalBaseClass obj, Delegate accessor, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            GetRef(obj, accessor).SetDirectAndStorePrev(
                Utils.Lerp(*(double*)historyA, *(double*)historyB, lerpTime), out *(double*)tempHistory);
    }

    internal unsafe class UserTypeProcessor<T> : ValueTypeProcessor<T> where T : unmanaged
    {
        private readonly InterpolatorDelegateWithReturn<T> _interpDelegate;

        internal override T GetInterpolatedValue(T prev, T current, float t) => _interpDelegate?.Invoke(prev, current, t) ?? current; 
        
        internal override void LoadHistory(InternalBaseClass obj, Delegate accessor, byte* tempHistory, byte* historyA, byte* historyB, float lerpTime) =>
            GetRef(obj, accessor).SetDirectAndStorePrev(
                _interpDelegate?.Invoke(*(T*)historyA, *(T*)historyB, lerpTime) ?? *(T*)historyA, out *(T*)tempHistory);

        public UserTypeProcessor(InterpolatorDelegateWithReturn<T> interpolationDelegate) =>
            _interpDelegate = interpolationDelegate;
    }
}