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

        emptySourceFile = Path.GetTempFileName();
        emptySourceHash = Utils.CalculateMD5(emptySourceFile);
        emptySourceDownloadToken = Guid.NewGuid().ToString();

        _tokenHandler.AddDownloadToken(sourceDownloadToken, sourceFile);
        _tokenHandler.AddDownloadToken(emptySourceDownloadToken, emptySourceFile);
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
    public async Task TestClientUploadWithCompressionHeader()
    {
        var uploadedFile = Path.GetTempFileName();
        var uploadToken = Guid.NewGuid().ToString();
        _tokenHandler.AddUploadToken(uploadToken, uploadedFile);
        try
        {
            var ftc = new FileTransferClient(Channel, _logger);
            await ftc.Upload(uploadToken, sourceFile, true, new List<Metadata.Entry> { new("grpc-internal-encoding-request", "gzip") }).ConfigureAwait(false);
            Utils.CalculateMD5(uploadedFile).Should().Be(sourceHash);
        }
        finally
        {
            File.Delete(uploadedFile);
        }
    }

    [Fact]
    public async Task TestClientUploadEmptyFile()
    {
        var uploadedFile = Path.GetTempFileName();
        var uploadToken = Guid.NewGuid().ToString();
        _tokenHandler.AddUploadToken(uploadToken, uploadedFile);
        try
        {
            var ftc = new FileTransferClient(Channel, _logger);
            await ftc.Upload(uploadToken, emptySourceFile, true).ConfigureAwait(false);
            Utils.CalculateMD5(uploadedFile).Should().Be(emptySourceHash);
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
    public async Task TestClientDownloadEmptyFile()
    {
        var downloadedFile = Path.GetTempFileName();
        try
        {
            var ftc = new FileTransferClient(Channel, _logger);
            await ftc.Download(emptySourceDownloadToken, downloadedFile, true).ConfigureAwait(false);
            Utils.CalculateMD5(downloadedFile).Should().Be(emptySourceHash);
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
        Func<Task> act = async () => { await ftc.Upload("abc", sourceFile).ConfigureAwait(false); };
        await act.Should().ThrowAsync<RpcException>(); //.Where(e => e.Status.StatusCode == StatusCode.FailedPrecondition);
    }

    public new void Dispose()
    {
        File.Delete(sourceFile);
        File.Delete(emptySourceFile);
        base.Dispose();
    }

    private readonly string sourceFile;
    private readonly string sourceHash;
    private const int testFileSizeInMB = 10;
    private readonly string emptySourceFile;
    private readonly string emptySourceHash;
    private readonly ILogger<FileTransferIntegrationTests> _logger;
    private readonly TransactionTokenHandler _tokenHandler;
    private readonly string sourceDownloadToken;
    private readonly string emptySourceDownloadToken;
}