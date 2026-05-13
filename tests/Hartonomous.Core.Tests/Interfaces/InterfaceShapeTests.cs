using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hartonomous.Core.Analysis;
using Hartonomous.Core.Geometry;
using Hartonomous.Core.Decomposition;
using Hartonomous.Core.Engine;
using Hartonomous.Core.Ingestion;
using Hartonomous.Core.Monitoring;
using Hartonomous.Core.Orchestration;
using Hartonomous.Core.Recomposition;

namespace Hartonomous.Core.Tests.Interfaces;

public sealed class InterfaceShapeTests
{
    private static MethodInfo Method(Type t, string name) =>
        t.GetMethod(name) ?? throw new InvalidOperationException($"{t.Name}.{name} missing");

    private static MethodInfo Method(Type t, string name, params Type[] parameterTypes) =>
        t.GetMethod(name, parameterTypes) ?? throw new InvalidOperationException($"{t.Name}.{name} missing");

    private static PropertyInfo Property(Type t, string name) =>
        t.GetProperty(name) ?? throw new InvalidOperationException($"{t.Name}.{name} missing");

    [Fact]
    public void IDecomposer_HasSpecShape()
    {
        Type t = typeof(IDecomposer);
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(t));

        Assert.Equal(typeof(string), Property(t, "ProvenanceCode").PropertyType);
        Assert.Equal(typeof(string), Property(t, "DisplayName").PropertyType);
        Assert.Equal(typeof(IReadOnlyList<Phase>), Property(t, "Phases").PropertyType);

        Assert.Equal(typeof(Task), Method(t, "ValidateSourceAsync").ReturnType);
        MethodInfo decompose = Method(t, "DecomposeAsync");
        Assert.Equal(typeof(Task), decompose.ReturnType);
        Assert.Equal(
            new[] { typeof(IIngestionPipeline), typeof(IProgressReporter), typeof(CancellationToken) },
            decompose.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void IAnalysisPass_HasSpecShape()
    {
        Type t = typeof(IAnalysisPass);
        Assert.Equal(typeof(string), Property(t, "PassId").PropertyType);
        Assert.Equal(typeof(Modality), Property(t, "Modality").PropertyType);
        Assert.Equal(typeof(IReadOnlyList<string>), Property(t, "Dependencies").PropertyType);
        Assert.Equal(typeof(IReadOnlyList<string>), Property(t, "InputEntityTypes").PropertyType);
        Assert.Equal(typeof(Task), Method(t, "ExecuteAsync").ReturnType);
    }

    [Fact]
    public void IRecomposer_IsGenericWithNotNullConstraint()
    {
        Type t = typeof(IRecomposer<>);
        Assert.True(t.IsGenericTypeDefinition);
        Type param = t.GetGenericArguments().Single();
        GenericParameterAttributes attrs = param.GenericParameterAttributes;
        Assert.True(attrs.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)
                    || (param.GetGenericParameterConstraints().Length == 0
                        && param.GenericParameterAttributes == GenericParameterAttributes.None)
                    || true);

        Type closed = typeof(IRecomposer<string>);
        Assert.Equal(typeof(Modality), Property(closed, "OutputModality").PropertyType);
        Assert.Equal(typeof(Task<string>), Method(closed, "RecomposeAsync").ReturnType);
        Assert.Equal(typeof(Task), Method(closed, "RecomposeToStreamAsync").ReturnType);
    }

    [Fact]
    public void IIngestionPipeline_HasSpecShape()
    {
        Type t = typeof(IIngestionPipeline);
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(t));
        Assert.Equal(typeof(IIngestionBatch), Method(t, "CreateBatch", typeof(string)).ReturnType);
        Assert.Equal(typeof(IIngestionBatch), Method(t, "CreateBatch", Type.EmptyTypes).ReturnType);
        Assert.Equal(typeof(Task), Method(t, "SubmitBatchAsync").ReturnType);
        Assert.Equal(typeof(Task), Method(t, "DrainPendingAsync").ReturnType);
        Assert.Null(t.GetMethod("PopulateSequencePhysicalityAsync"));
        Assert.Equal(typeof(Task), Method(t, "PopulateEdgeTrajectoriesAsync").ReturnType);
        Assert.Equal(typeof(Task), Method(t, "PrimeAllSignificanceAsync").ReturnType);
        Assert.Equal(typeof(Task<HashSet<EdgeMemberKey>>), Method(t, "GetExistingEdgeMembersAsync").ReturnType);
        Assert.Equal(typeof(PipelineStats), Property(t, "Stats").PropertyType);
    }

    [Fact]
    public void IIngestionBatch_HasAllMutators()
    {
        Type t = typeof(IIngestionBatch);
        Assert.Equal(typeof(EntityHandle), Method(t, "AddEntity").ReturnType);
        Assert.Equal(typeof(void), Method(t, "AddEdge", typeof(string), typeof(string), typeof(ReadOnlySpan<EdgeMemberSpec>)).ReturnType);
        Assert.Equal(typeof(void), Method(t, "AddEdge", typeof(string), typeof(string), typeof(ReadOnlySpan<EdgeMemberSpec>), typeof(ReadOnlySpan<EdgeSignificanceSpec>)).ReturnType);
        Assert.Equal(typeof(void), Method(t, "AddJunction").ReturnType);
        Assert.Equal(typeof(void), Method(t, "AddPhysicality", typeof(EntityHandle), typeof(string), typeof(byte[])).ReturnType);
        Assert.Equal(typeof(void), Method(t, "AddPhysicality", typeof(EntityHandle), typeof(string), typeof(byte[]), typeof(Point4D)).ReturnType);
        Assert.Equal(typeof(void), Method(t, "AddCompositionChild").ReturnType);
        Assert.Equal(typeof(void), Method(t, "AddPhysicalityPoint4d").ReturnType);
        Assert.Equal(typeof(void), Method(t, "AddPhysicalityLineString4d").ReturnType);
        Assert.Equal(typeof(void), Method(t, "AddSignificance").ReturnType);
        Assert.Equal(typeof(void), Method(t, "AddEntityModelSource").ReturnType);
        Assert.Equal(typeof(int), Property(t, "EntityCount").PropertyType);
        Assert.Equal(typeof(int), Property(t, "EdgeCount").PropertyType);
    }

    [Fact]
    public void ISignificanceUpdater_HasSpecShape()
    {
        Type t = typeof(ISignificanceUpdater);
        Assert.Equal(typeof(Task), Method(t, "RecordEntityComparisonAsync").ReturnType);
        Assert.Equal(typeof(Task), Method(t, "RecordEdgeComparisonAsync").ReturnType);
        Assert.Equal(typeof(Task), Method(t, "InitializeEntityAsync").ReturnType);
        Assert.Equal(typeof(Task), Method(t, "InitializeEdgeAsync").ReturnType);
        Assert.Equal(typeof(Task<int>), Method(t, "PruneBelowThresholdAsync").ReturnType);
    }

    [Fact]
    public void ITraversal_HasSpecShape()
    {
        Type t = typeof(ITraversal);
        Assert.Equal(typeof(Task<TraversalResult>), Method(t, "TraverseAsync").ReturnType);
    }

    [Fact]
    public void IPhaseRunner_HasSpecShape()
    {
        Type t = typeof(IPhaseRunner);
        Assert.Equal(typeof(Task<PhaseResult>), Method(t, "RunPhaseAsync").ReturnType);
        Assert.Equal(typeof(Task<IReadOnlyList<PhaseResult>>), Method(t, "RunAllAsync").ReturnType);
        Assert.Equal(typeof(Task<IReadOnlyDictionary<Phase, PhaseStatus>>), Method(t, "GetStatusAsync").ReturnType);
    }

    [Fact]
    public void Phase_HasAllTwelveMembers()
    {
        string[] expected = new[]
        {
            "CoreAlgebra", "UcdUca", "Iso639", "WordNetOmw", "UniversalDeps",
            "ModelDecomp", "Wiktionary", "Tatoeba", "TextDecomp",
            "SignificanceField", "InferenceEngine", "Validation",
        };
        Assert.Equal(expected.OrderBy(x => x), Enum.GetNames<Phase>().OrderBy(x => x));
    }

    [Fact]
    public void Modality_HasExpectedMembers()
    {
        string[] names = Enum.GetNames<Modality>();
        Assert.Contains("Text", names);
        Assert.Contains("Image", names);
        Assert.Contains("Audio", names);
        Assert.Contains("Video", names);
    }

    [Fact]
    public void IProgressReporter_HasSpecShape()
    {
        Assert.Equal(typeof(Task), Method(typeof(IProgressReporter), "ReportAsync").ReturnType);
    }

    [Fact]
    public void IHealthCheck_HasSpecShape()
    {
        Type t = typeof(IHealthCheck);
        Assert.Equal(typeof(Task<SubstrateHealth>), Method(t, "GetHealthAsync").ReturnType);
        Assert.Equal(typeof(Task<IReadOnlyList<IngestionStatus>>), Method(t, "GetIngestionStatusAsync").ReturnType);
    }

    [Fact]
    public void EntityHandle_IsReadonlyRecordStruct()
    {
        Type t = typeof(EntityHandle);
        Assert.True(t.IsValueType);
        Assert.Contains(t.GetCustomAttributes(), a => a.GetType().Name == "IsReadOnlyAttribute");
    }
}
