using System.Diagnostics;
using System.Reflection;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Pulsar.GrpcFileTransfer.Service;
using Pulsar.GrpcFileTransfer.ServiceInterface;

namespace Pulsar.GrpcFileTransfer.Test;

public class FileTransferServiceTestImplementation : AbstractFileTransferService
{
    public const string ServiceName = "Test file transfer";
    public const string ServiceDescription = "File transfer service for test server";
    public static readonly string ServiceVersion = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion ?? "0.0.0";

    public FileTransferServiceTestImplementation(TransactionTokenHandler tokenHandler, ILoggerFactory loggerFactory) : base(loggerFactory.CreateLogger<FileTransferServiceTestImplementation>())
    {
        _tokenHandler = tokenHandler;
    }

    protected override Task<ServiceInformation> GetInfoImplementation(Empty request, ServerCallContext context)
    {
        return Task.FromResult(new ServiceInformation
        {
            Name = ServiceName,
            Description = ServiceDescription,
            Version = ServiceVersion
        });
    }

    protected override Task<PreHookResponse> PreUploadHook(string fileID)
    {
        if (_tokenHandler.UploadTokenDictionary.ContainsKey(fileID))
        {
            var filePath = _tokenHandler.UploadTokenDictionary[fileID];
            return Task.FromResult(new PreHookResponse(true, filePath, string.Empty));
        }

        return Task.FromResult(new PreHookResponse(false, string.Empty, $"No file found for fileID {fileID}"));
    }

    protected override Task<PreHookResponse> PreDownloadHook(string fileID)
    {
        if (_tokenHandler.DownloadTokenDictionary.ContainsKey(fileID))
        {
            var filePath = _tokenHandler.DownloadTokenDictionary[fileID];
            return Task.FromResult(new PreHookResponse(true, filePath, string.Empty));
        }

        return Task.FromResult(new PreHookResponse(false, string.Empty, $"No file found for fileID {fileID}"));
    }

    protected override Task<ValidationResult> ValidateHeaders(Metadata headers)
    {
        return Task.FromResult(new ValidationResult(true, string.Empty));
    }

    private readonly TransactionTokenHandler _tokenHandler;
}