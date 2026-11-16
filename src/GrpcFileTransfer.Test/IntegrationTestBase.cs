using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pulsar.GrpcFileTransfer.Test.Helpers;

namespace Pulsar.GrpcFileTransfer.Test;

public abstract class IntegrationTestBase : IDisposable
{
    private GrpcChannel? _channel;
    private readonly IDisposable _logForwarding;

    protected IntegrationTestBase(TestContext testContext)
    {
        TestContext = testContext;
        _logForwarding = Fixture.GetTestContext(testContext);
    }

    /// <summary>The running test's context: its output and cancellation token.</summary>
    protected TestContext TestContext { get; }

    protected static GrpcTestFixture<Startup> Fixture => SharedGrpcServer.Fixture;

    protected ILoggerFactory LoggerFactory => Fixture.LoggerFactory;

    protected GrpcChannel Channel => _channel ??= CreateChannel();

    protected GrpcChannel CreateChannel()
    {
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            LoggerFactory = LoggerFactory,
            HttpHandler = Fixture.Handler
        });
    }

    public virtual void Dispose()
    {
        _logForwarding.Dispose();
        _channel?.Dispose();
        _channel = null;
        GC.SuppressFinalize(this);
    }
}

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddGrpc();
        services.AddSingleton<FileTransferServiceTestImplementation>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
            app.UseDeveloperExceptionPage();

        app.UseRouting();
        app.UseEndpoints(endpoints => { endpoints.MapGrpcService<FileTransferServiceTestImplementation>(); });
    }
}