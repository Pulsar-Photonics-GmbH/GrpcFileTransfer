[![BuildTest](https://github.com/Pulsar-Photonics/GrpcFileTransfer/actions/workflows/dotnet-build-test-deploy.yml/badge.svg?branch=main)](https://github.com/Pulsar-Photonics/GrpcFileTransfer/actions/workflows/dotnet-build-test-deploy.yml?query=branch%3Amain)
[![nuget](https://img.shields.io/nuget/v/Pulsar.GrpcFileTransfer.svg?label=nuget)](https://www.nuget.org/packages/Pulsar.GrpcFileTransfer)
# File transfer via gRPC in .NET
GrpcFileTransfer is a .NET library to transfer files between machines using protobuf & gRPC.

## Getting started
To use this library, follow this three main steps:
- Install in your project from nuget [![nuget](https://img.shields.io/nuget/v/Pulsar.GrpcFileTransfer.svg?label=nuget)](https://www.nuget.org/packages/Pulsar.GrpcFileTransfer)
- Implement the server side service using the abstract [`AbstractFileTransferService`](src/GrpcFileTransfer/Service/AbstractFileTransferService.cs) class
- Register the service
- Use the [`FileTransferClient`](src/GrpcFileTransfer/Client/FileTransferClient.cs) to up- & download files

## Implementing the server class
The base class [`AbstractFileTransferService`](src/GrpcFileTransfer/Service/AbstractFileTransferService.cs) for the server side code is abstract and needs to be implemented to provide at least a response to the `GetInfo` api call in [`FileTransferService.proto`](src/GrpcFileTransfer/FileTransferService.proto).

A minimal implementation could look like this:

```csharp
public class FileTransferService : AbstractFileTransferService
{
    protected override Task<ServiceInformation> GetInfoImplementation(Empty request, ServerCallContext context)
    {
        return Task.FromResult(new ServiceInformation
        {
            Name = "File Transfer Service",
            Description = "Transfer files over gRPC",
            Version = "1.2.3
        });
    }
}
```

All logic for actually handling up- & and downloads is contained in the abstract class.

To add custom logic before / after downloads, the [`AbstractFileTransferService`](src/GrpcFileTransfer/Service/AbstractFileTransferService.cs) offers the following virtual functions that can be overriden if required:
- `ValidateHeaders`, which is called before every up- or download and gets passed the gRPC headers. This can be used for custom header validation, e.g. to validate an authorization header.
- `PreUploadHook` called before an upload.
- `PostUploadHook` called after an upload.
- `PreDownloadHook` called before an download.
- `PostDownloadHook` called after an download.

For an example on how to utilize this functions, see the [`FileTransferServiceTestImplementation.cs`](src/GrpcFileTransfer.Test/FileTransferServiceTestImplementation.cs) in the integration tests. It demonstrates how the client does not need to be aware of the actual filepath on the server. Instead, a file token is passed as `fileId` to the `Upload` and `Download` functions on the client side and the overriden Pre- and PostHooks functions are used to map the token to the server file path.

## Registering the service
When using `asp.net core`, the implemented service class needs to be registered just like any other grpc service.

```csharp
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddGrpc();
        services.AddSingleton<FileTransferService>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
            app.UseDeveloperExceptionPage();

        app.UseRouting();
        app.UseEndpoints(endpoints => { endpoints.MapGrpcService<FileTransferService>(); });
    }
}
```

For more information, see the corresponding Microsoft help page: https://learn.microsoft.com/en-us/aspnet/core/grpc/aspnetcore?view=aspnetcore-7.0#configure-grpc

## Using the client
Using the client is straightforward - create an instance, call the up- & download functions:

```csharp
var client = new FileTransferClient(grpcChannel, _logger);
await client.Upload(targetFile, sourceFile, hashVerification: true).ConfigureAwait(false);
await client.Download(targetFile, downloadedFile, hashVerification: true).ConfigureAwait(false);
```

See also the corresponding tests for a ready-to-run example: [`FileTransferIntegrationTests.cs`](src/GrpcFileTransfer.Test/FileTransferIntegrationTests.cs).
