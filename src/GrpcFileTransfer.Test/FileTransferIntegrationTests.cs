using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Pulsar.GrpcFileTransfer.Client;
using Pulsar.GrpcFileTransfer.ServiceInterface;

namespace Pulsar.GrpcFileTransfer.Test;

[TestClass]
public sealed class FileTransferIntegrationTests : IntegrationTestBase
{
    private const int TestFileSizeInMB = 10;

    private readonly ILogger<FileTransferIntegrationTests> _logger;
    private readonly TransactionTokenHandler _tokenHandler;
    private readonly string _sourceFile;
    private readonly string _sourceHash;
    private readonly string _sourceDownloadToken;
    private readonly string _emptySourceFile;
    private readonly string _emptySourceHash;
    private readonly string _emptySourceDownloadToken;

    public FileTransferIntegrationTests(TestContext testContext) : base(testContext)
    {
        _logger = LoggerFactory.CreateLogger<FileTransferIntegrationTests>();
        _tokenHandler = Fixture.TokenHandler;

        _sourceFile = Path.GetTempFileName();
        using (var fs = new FileStream(_sourceFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.SetLength(TestFileSizeInMB * 1024 * 1024);
        }
        _sourceHash = Utils.CalculateMD5(_sourceFile);
        _sourceDownloadToken = Guid.NewGuid().ToString();

        _emptySourceFile = Path.GetTempFileName();
        _emptySourceHash = Utils.CalculateMD5(_emptySourceFile);
        _emptySourceDownloadToken = Guid.NewGuid().ToString();

        _tokenHandler.AddDownloadToken(_sourceDownloadToken, _sourceFile);
        _tokenHandler.AddDownloadToken(_emptySourceDownloadToken, _emptySourceFile);
    }

    private CancellationToken CancellationToken => TestContext.CancellationToken;

    [TestMethod]
    public async Task ApiInformationTest()
    {
        var grpcClient = new FileTransfer.FileTransferClient(Channel);
        var response = await grpcClient.GetInfoAsync(new Empty(), cancellationToken: CancellationToken);

        Assert.IsNotNull(response);
        Assert.AreEqual(FileTransferServiceTestImplementation.ServiceName, response.Name);
        Assert.AreEqual(FileTransferServiceTestImplementation.ServiceDescription, response.Description);
        Assert.AreEqual(FileTransferServiceTestImplementation.ServiceVersion, response.Version);
    }

    [TestMethod]
    public async Task TestClientUpload()
    {
        var uploadedFile = Path.GetTempFileName();
        var uploadToken = Guid.NewGuid().ToString();
        _tokenHandler.AddUploadToken(uploadToken, uploadedFile);
        try
        {
            var ftc = new FileTransferClient(Channel, _logger);
            await ftc.Upload(uploadToken, _sourceFile, true, cancellationToken: CancellationToken);
            Assert.AreEqual(_sourceHash, Utils.CalculateMD5(uploadedFile));
        }
        finally
        {
            File.Delete(uploadedFile);
        }
    }

    [TestMethod]
    public async Task TestClientUploadWithCompressionHeader()
    {
        var uploadedFile = Path.GetTempFileName();
        var uploadToken = Guid.NewGuid().ToString();
        _tokenHandler.AddUploadToken(uploadToken, uploadedFile);
        try
        {
            var ftc = new FileTransferClient(Channel, _logger);
            await ftc.Upload(uploadToken, _sourceFile, true, [new("grpc-internal-encoding-request", "gzip")], CancellationToken);
            Assert.AreEqual(_sourceHash, Utils.CalculateMD5(uploadedFile));
        }
        finally
        {
            File.Delete(uploadedFile);
        }
    }

    [TestMethod]
    public async Task TestClientUploadEmptyFile()
    {
        var uploadedFile = Path.GetTempFileName();
        var uploadToken = Guid.NewGuid().ToString();
        _tokenHandler.AddUploadToken(uploadToken, uploadedFile);
        try
        {
            var ftc = new FileTransferClient(Channel, _logger);
            await ftc.Upload(uploadToken, _emptySourceFile, true, cancellationToken: CancellationToken);
            Assert.AreEqual(_emptySourceHash, Utils.CalculateMD5(uploadedFile));
        }
        finally
        {
            File.Delete(uploadedFile);
        }
    }

    [TestMethod]
    public async Task TestClientDownload()
    {
        var downloadedFile = Path.GetTempFileName();
        try
        {
            var ftc = new FileTransferClient(Channel, _logger);
            await ftc.Download(_sourceDownloadToken, downloadedFile, true, cancellationToken: CancellationToken);
            Assert.AreEqual(_sourceHash, Utils.CalculateMD5(downloadedFile));
        }
        finally
        {
            File.Delete(downloadedFile);
        }
    }

    [TestMethod]
    public async Task TestClientDownloadEmptyFile()
    {
        var downloadedFile = Path.GetTempFileName();
        try
        {
            var ftc = new FileTransferClient(Channel, _logger);
            await ftc.Download(_emptySourceDownloadToken, downloadedFile, true, cancellationToken: CancellationToken);
            Assert.AreEqual(_emptySourceHash, Utils.CalculateMD5(downloadedFile));
        }
        finally
        {
            File.Delete(downloadedFile);
        }
    }

    [TestMethod]
    public async Task TestBadDownloadToken()
    {
        var ftc = new FileTransferClient(Channel, _logger);
        var exception = await Assert.ThrowsAsync<RpcException>(
            () => ftc.Download("abc", "def", cancellationToken: CancellationToken));
        Assert.AreEqual(StatusCode.FailedPrecondition, exception.StatusCode);
    }

    [TestMethod]
    public async Task TestBadUploadToken()
    {
        var ftc = new FileTransferClient(Channel, _logger);
        await Assert.ThrowsAsync<RpcException>(
            () => ftc.Upload("abc", _sourceFile, cancellationToken: CancellationToken));
    }

    public override void Dispose()
    {
        File.Delete(_sourceFile);
        File.Delete(_emptySourceFile);
        base.Dispose();
    }
}
