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

    protected override bool PreUploadHook(string fileID, out string filePath, out string message)
    {
        if (_tokenHandler.UploadTokenDictionary.ContainsKey(fileID))
        {
            message = string.Empty;
            filePath = _tokenHandler.UploadTokenDictionary[fileID];
            return true;
        }

        filePath = string.Empty;
        message = $"No file found for fileID {fileID}";
        return false;
    }

    protected override bool PreDownloadHook(string fileID, out string filePath, out string message)
    {
        if (_tokenHandler.DownloadTokenDictionary.ContainsKey(fileID))
        {
            message = string.Empty;
            filePath = _tokenHandler.DownloadTokenDictionary[fileID];
            return true;
        }

        filePath = string.Empty;
        message = $"No file found for fileID {fileID}";
        return false;
    }

    protected override bool ValidateHeaders(Metadata headers, out string message)
    {
        message = string.Empty;
        return true;
    }

    private readonly TransactionTokenHandler _tokenHandler;
}