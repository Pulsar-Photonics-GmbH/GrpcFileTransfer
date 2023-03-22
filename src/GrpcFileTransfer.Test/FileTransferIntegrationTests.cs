using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Pulsar.GrpcFileTransfer.Client;
using Pulsar.GrpcFileTransfer.ServiceInterface;
using Pulsar.GrpcFileTransfer.Test.Helpers;
using Xunit.Abstractions;

namespace Pulsar.GrpcFileTransfer.Test;

public class FileTransferIntegrationTests : IntegrationTestBase, IDisposable
{
    public FileTransferIntegrationTests(GrpcTestFixture<Startup> fixture, ITestOutputHelper outputHelper) : base(
        fixture, outputHelper)
    {
        _logger = fixture.LoggerFactory.CreateLogger<FileTransferIntegrationTests>();
        _tokenHandler = fixture.TokenHandler;
        sourceFile = Path.GetTempFileName();

        using (var fs = new FileStream(sourceFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(testFileSizeInMB * 1024 * 1024);
        }

        sourceHash = Utils.CalculateMD5(sourceFile);
        sourceDownloadToken = Guid.NewGuid().ToString();
        _tokenHandler.AddDownloadToken(sourceDownloadToken, sourceFile);
    }

    [Fact]
    public Task ApiInformationTest()
    {
        var _grpcClient = new FileTransfer.FileTransferClient(Channel);
        var response = _grpcClient.GetInfo(new Google.Protobuf.WellKnownTypes.Empty());
        response.Should().NotBeNull();
        response.Name.Should().Be(FileTransferServiceTestImplementation.ServiceName);
        response.Description.Should().Be(FileTransferServiceTestImplementation.ServiceDescription);
        response.Version.Should().Be(FileTransferServiceTestImplementation.ServiceVersion);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TestUpload()
    {
        var _grpcClient = new FileTransfer.FileTransferClient(Channel);
        var uploadedFile = Path.GetTempFileName();
        var uploadToken = Guid.NewGuid().ToString();
        _tokenHandler.AddUploadToken(uploadToken, uploadedFile);
        try
        {
            await using var fileStreamUpload = File.OpenRead(sourceFile);

            int chunkSizeBytes = 1024 * 1024; // 1MB
            byte[] buffer = new byte[chunkSizeBytes];

            var abc = _grpcClient.Upload();
            await abc.RequestStream.WriteAsync(new FileUploadRequest { FileId = uploadToken }).ConfigureAwait(false);

            var chunk = new FileChunk();
            for (long i = 0; i < fileStreamUpload.Length; i += chunkSizeBytes)
            {
                if (fileStreamUpload.Position + chunkSizeBytes >= fileStreamUpload.Length)
                {
                    chunkSizeBytes = (int)(fileStreamUpload.Length - fileStreamUpload.Position);
                    buffer = new byte[chunkSizeBytes];
                    chunk.IsLast = true;
                }

                await fileStreamUpload.ReadExactlyAsync(buffer, 0, chunkSizeBytes).ConfigureAwait(false);
                chunk.Content = UnsafeByteOperations.UnsafeWrap(buffer);

                chunk.Id++;

                await abc.RequestStream.WriteAsync(new FileUploadRequest { FileChunk = chunk }).ConfigureAwait(false);
            }

            await abc.RequestStream.WriteAsync(new FileUploadRequest { GetMd5Hash = true }).ConfigureAwait(false);

            string hashUpload = Utils.CalculateMD5(sourceFile);
            await abc.RequestStream.CompleteAsync().ConfigureAwait(false);
            var responseUpload = await abc.ResponseAsync.ConfigureAwait(false);
            hashUpload.Should().Be(responseUpload.Md5Hash);
            hashUpload.Should().Be(sourceHash);
        }
        finally
        {
            File.Delete(uploadedFile);
        }
    }

    [Fact]
    public async Task TestDownload()
    {
        var _grpcClient = new FileTransfer.FileTransferClient(Channel);

        var downloadedFile = Path.GetTempFileName();

        try
        {
            var responseDownload = _grpcClient.Download(new FileDownloadRequest
            {
                GetMd5Hash = true,
                FileID = sourceDownloadToken
            });

            var responseStream = responseDownload.ResponseStream;
            await using var fileStreamDownload = File.OpenWrite(downloadedFile);
            string serverHash = string.Empty;

            while (await responseStream.MoveNext(default).ConfigureAwait(false))
            {
                switch (responseStream.Current.ResponseCase)
                {
                    case FileDownloadResponse.ResponseOneofCase.FileChunk:
                        if (fileStreamDownload is not { CanWrite: true })
                            throw new Exception("file not writeable");

                        await fileStreamDownload.WriteAsync(responseStream.Current.FileChunk.Content.Memory)
                            .ConfigureAwait(false);
                        if (responseStream.Current.FileChunk.IsLast)
                            await fileStreamDownload.DisposeAsync().ConfigureAwait(false);

                        break;
                    case FileDownloadResponse.ResponseOneofCase.Md5Hash:
                        serverHash = responseStream.Current.Md5Hash;

                        break;
                    case FileDownloadResponse.ResponseOneofCase.None:
                    default:
                        throw new Exception(
                            $"Unknown response case {Enum.GetName(responseStream.Current.ResponseCase)}");
                }
            }


            string hashDownload = Utils.CalculateMD5(downloadedFile);
            serverHash.Should().Be(hashDownload);
            hashDownload.Should().Be(sourceHash);
        }
        finally
        {
            File.Delete(downloadedFile);
        }
    }

    [Fact]
    public async Task TestClientUpload()
    {
        var uploadedFile = Path.GetTempFileName();
        var uploadToken = Guid.NewGuid().ToString();
        _tokenHandler.AddUploadToken(uploadToken, uploadedFile);
        try
        {
            var ftc = new FileTransferClient(Channel, _logger);
            await ftc.Upload(uploadToken, sourceFile, true).ConfigureAwait(false);
            Utils.CalculateMD5(uploadedFile).Should().Be(sourceHash);
        }
        finally
        {
            File.Delete(uploadedFile);
        }
    }

    [Fact]
    public async Task TestClientDownload()
    {
        var downloadedFile = Path.GetTempFileName();
        try
        {
            var ftc = new FileTransferClient(Channel, _logger);
            await ftc.Download(sourceDownloadToken, downloadedFile, true).ConfigureAwait(false);
            Utils.CalculateMD5(downloadedFile).Should().Be(sourceHash);
        }
        finally
        {
            File.Delete(downloadedFile);
        }
    }

    [Fact]
    public async void TestBadDownloadToken()
    {
        var ftc = new FileTransferClient(Channel, _logger);
        Func<Task> act = async () => { await ftc.Download("abc", "def").ConfigureAwait(false); };
        await act.Should().ThrowAsync<RpcException>().Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    [Fact]
    public async void TestBadUploadToken()
    {
        var ftc = new FileTransferClient(Channel, _logger);
        Func<Task> act = async () => { await ftc.Download("abc", sourceFile).ConfigureAwait(false); };
        await act.Should().ThrowAsync<RpcException>().Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    public new void Dispose()
    {
        File.Delete(sourceFile);
        base.Dispose();
    }

    private readonly string sourceFile;
    private readonly string sourceHash;
    private const int testFileSizeInMB = 10;
    private readonly ILogger<FileTransferIntegrationTests> _logger;
    private readonly TransactionTokenHandler _tokenHandler;
    private readonly string sourceDownloadToken;
}