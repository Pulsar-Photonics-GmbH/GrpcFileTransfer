using Pulsar.GrpcFileTransfer.Test.Helpers;

namespace Pulsar.GrpcFileTransfer.Test;

/// <summary>Starts one in-process gRPC server for the whole test run.</summary>
[TestClass]
public static class SharedGrpcServer
{
    private static GrpcTestFixture<Startup>? s_fixture;

    public static GrpcTestFixture<Startup> Fixture =>
        s_fixture ?? throw new InvalidOperationException("The test server has not been started.");

    [AssemblyInitialize]
    public static void Start(TestContext testContext) => s_fixture = new GrpcTestFixture<Startup>();

    [AssemblyCleanup]
    public static void Stop()
    {
        s_fixture?.Dispose();
        s_fixture = null;
    }
}
