using LiteEntitySystem;
using LiteEntitySystem.Internal;

namespace AnalyzerTest;

public enum AccessorTestState
{
    Idle,
    Running,
    Done
}

/// <summary>
/// Test syncable field that declares its own sync vars.
/// </summary>
public partial class AccessorTestSyncable : SyncableField
{
    private SyncVar<float> _value;

    public float ReadValue() => _value.Value;

    public void WriteValue(float value) => _value.Value = value;

    public void CollectAccessors(SyncVarAccessorMap map) => RegisterSyncVarAccessors(map);
}

/// <summary>
/// Test entity declared in a *different assembly* than the framework. This verifies that the generator
/// can emit the accessor override into a consumer assembly.
/// </summary>
public partial class AccessorTestEntity : InternalBaseClass
{
    private SyncVar<int> _health;
    private SyncVar<AccessorTestState> _state;

    public readonly AccessorTestSyncable Field = new();

    public int ReadHealth() => _health.Value;

    public void WriteHealth(int value) => _health.Value = value;

    public AccessorTestState ReadState() => _state.Value;

    public void WriteState(AccessorTestState value) => _state.Value = value;

    public void CollectAccessors(SyncVarAccessorMap map) => RegisterSyncVarAccessors(map);
}

/// <summary>
/// Type that supplies its accessors by hand instead of relying on the generator. This is the escape hatch for
/// assemblies/types the generator does not process - and because the override is hand written, the type does
/// not have to be <c>partial</c>.
/// </summary>
public class ManualAccessorEntity : InternalBaseClass
{
    private SyncVar<long> _score;

    public long ReadScore() => _score.Value;

    public void CollectAccessors(SyncVarAccessorMap map) => RegisterSyncVarAccessors(map);

    protected override void RegisterSyncVarAccessors(SyncVarAccessorMap map) =>
        map.Add(typeof(ManualAccessorEntity), "_score",
            new SyncVarRefGetter<long>(entity => ref ((ManualAccessorEntity)entity)._score));
}

[TestClass]
public class GeneratedAccessorTest
{
    private static AccessorTestEntity CreateEntity() => new();
    [TestMethod]
    public void GeneratedAccessorRefersToDeclaredSyncVarField()
    {
        var entity = CreateEntity();
        var map = new SyncVarAccessorMap();
        entity.CollectAccessors(map);

        Assert.IsTrue(map.TryGet(typeof(AccessorTestEntity), "_health", out var accessor),
            "Generator did not register an accessor for _health");
        Assert.IsInstanceOfType(accessor, typeof(SyncVarRefGetter<int>));

        ref var syncVar = ref ((SyncVarRefGetter<int>)accessor)(entity);
        syncVar.Value = 1234;

        Assert.AreEqual(1234, entity.ReadHealth());
    }

    [TestMethod]
    public void GeneratedAccessorWorksForEnumBackedField()
    {
        var entity = CreateEntity();
        var map = new SyncVarAccessorMap();
        entity.CollectAccessors(map);

        Assert.IsTrue(map.TryGet(typeof(AccessorTestEntity), "_state", out var accessor),
            "Generator did not register an accessor for _state");

        ref var syncVar = ref ((SyncVarRefGetter<AccessorTestState>)accessor)(entity);
        syncVar.Value = AccessorTestState.Running;

        Assert.AreEqual(AccessorTestState.Running, entity.ReadState());
    }

    [TestMethod]
    public void GeneratedAccessorReturnsSyncableFieldInstance()
    {
        var entity = CreateEntity();
        var map = new SyncVarAccessorMap();
        entity.CollectAccessors(map);

        Assert.IsTrue(map.TryGet(typeof(AccessorTestEntity), nameof(AccessorTestEntity.Field), out var accessor),
            "Generator did not register an accessor for the syncable field");
        Assert.IsInstanceOfType(accessor, typeof(ObjectFieldGetter<SyncableField>));

        var syncable = ((ObjectFieldGetter<SyncableField>)accessor)(entity);
        Assert.AreSame(entity.Field, syncable);
    }

    [TestMethod]
    public void GeneratedAccessorForFieldInsideSyncableField()
    {
        var entity = CreateEntity();
        var syncableMap = new SyncVarAccessorMap();
        entity.Field.CollectAccessors(syncableMap);

        Assert.IsTrue(syncableMap.TryGet(typeof(AccessorTestSyncable), "_value", out var accessor),
            "Generator did not register an accessor for the syncable field's _value");
        Assert.IsInstanceOfType(accessor, typeof(SyncVarRefGetter<float>));

        ref var syncVar = ref ((SyncVarRefGetter<float>)accessor)(entity.Field);
        syncVar.Value = 2.5f;

        Assert.AreEqual(2.5f, entity.Field.ReadValue());
    }

    [TestMethod]
    public void HandWrittenAccessorOverrideIsUsedInsteadOfGeneratedOne()
    {
        var entity = new ManualAccessorEntity();
        var map = new SyncVarAccessorMap();
        entity.CollectAccessors(map);

        Assert.IsTrue(map.TryGet(typeof(ManualAccessorEntity), "_score", out var accessor));

        ref var syncVar = ref ((SyncVarRefGetter<long>)accessor)(entity);
        syncVar.Value = 99L;

        Assert.AreEqual(99L, entity.ReadScore());
    }
}
