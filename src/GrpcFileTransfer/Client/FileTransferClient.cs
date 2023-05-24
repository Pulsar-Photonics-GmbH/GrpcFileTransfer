using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Pulsar.GrpcFileTransfer.Exceptions;
using Pulsar.GrpcFileTransfer.ServiceInterface;

namespace Pulsar.GrpcFileTransfer.Client;

public class FileTransferClient
{
    public FileTransferClient(GrpcChannel grpcChannel)
    {
        _grpcClient = new InternalFileTransfer.InternalFileTransferClient(grpcChannel);
    }

    public FileTransferClient(InternalFileTransfer.InternalFileTransferClient grpcClient)
    {
        _grpcClient = grpcClient;
    }

    public FileTransferClient(GrpcChannel grpcChannel, ILogger logger) : this(grpcChannel)
    {
        _logger = logger;
    }

    public FileTransferClient(InternalFileTransfer.InternalFileTransferClient grpcClient, ILogger logger) : this(grpcClient)
    {
        _logger = logger;
    }

    /// <summary>
    /// Download file from server.
    /// </summary>
    /// <param name="fileId">FileId of the file on the server.</param>
    /// <param name="localFilepath">Local filepath to save file to.</param>
    /// <param name="hashVerification">If file integrity should be checked after transfer using md5 hashes.</param>
    /// <param name="headerFields">Additional header fields to pass in the gRPC call.</param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="AccessViolationException">If <see cref="localFilepath"/> is not writeable.</exception>
    /// <exception cref="CorruptedFileException">When <see cref="hashVerification"/> is set and hashes do not match.</exception>
    /// <exception cref="TransferFailedException">If the download failed for another reason.</exception>
    public async Task Download(string fileId, string localFilepath, bool hashVerification = false, IEnumerable<Metadata.Entry>? headerFields = null, CancellationToken cancellationToken = default)
    {
        var headers = new Metadata();
        if (headerFields != null)
            foreach (var headerField in headerFields)
                headers.Add(headerField);

        var response = _grpcClient.Download(new FileDownloadRequest
            {
                GetMd5Hash = hashVerification,
                FileId = fileId
            },
            headers);

        var responseStream = response.ResponseStream;
        await using var fileStream = File.OpenWrite(localFilepath);
        string serverHash = string.Empty;
        bool downloadSuccess = false;

        _logger?.LogDebug("Starting download of fileId {} to {}", fileId, localFilepath);

        while (await responseStream.MoveNext(default).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (responseStream.Current.ResponseCase)
            {
                case FileDownloadResponse.ResponseOneofCase.FileChunk:
                    if (fileStream is not { CanWrite: true })
                        throw new AccessViolationException($"No write permission for {localFilepath}.");

                    await fileStream.WriteAsync(responseStream.Current.FileChunk.Content.Memory, cancellationToken).ConfigureAwait(false);
                    _logger?.LogTrace("Received chunk {}", responseStream.Current.FileChunk.Id);
                    if (responseStream.Current.FileChunk.IsLast)
                    {
                        await fileStream.DisposeAsync().ConfigureAwait(false);
                        downloadSuccess = true;
                        _logger?.LogTrace("Chunk {} is marked as last chunk", responseStream.Current.FileChunk.Id);
                    }

                    break;
                case FileDownloadResponse.ResponseOneofCase.Md5Hash:
                    serverHash = responseStream.Current.Md5Hash;
                    _logger?.LogTrace("Received server side md5 hash for fileId {}: {}", fileId, serverHash);

                    break;
                case FileDownloadResponse.ResponseOneofCase.None:
                default:
                    break;
            }
        }

        if (hashVerification)
        {
            string hash = Utils.CalculateMD5(localFilepath);
            _logger?.LogTrace("Calculated local md5 hash for {}: {}", localFilepath, hash);

            if (hash != serverHash)
            {
                var ex = new CorruptedFileException(localFilepath, serverHash, hash, "");
                _logger?.LogError(ex, "Hash comparison failed");
                throw ex;
            }
        }

        if (!downloadSuccess)
        {
            if (File.Exists(localFilepath))
            {
                File.Delete(localFilepath);
                _logger?.LogTrace("Deleted {} because download failed.", localFilepath);
            }

            var ex = new TransferFailedException(localFilepath, "Download failed with unknown reason.");
            _logger?.LogError(ex, "Transfer of {} failed.", localFilepath);
        }

        _logger?.LogDebug("Finished download of fileId {} to {}", fileId, localFilepath);
    }

    /// <summary>
    /// Upload file to server.
    /// </summary>
    /// <param name="fileId">FileId of the file on the server.</param>
    /// <param name="localFilepath">Local filepath of the file to upload.</param>
    /// <param name="hashVerification">If file integrity should be checked after transfer using md5 hashes.</param>
    /// <param name="headerFields">Additional header fields to pass in the gRPC call.</param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="TransferFailedException">If the download failed for another reason.</exception>
    public async Task Upload(string fileId, string localFilepath, bool hashVerification = false, IEnumerable<Metadata.Entry>? headerFields = null, CancellationToken cancellationToken = default)
    {
        var headers = new Metadata();
        if (headerFields != null)
            foreach (var headerField in headerFields)
                headers.Add(headerField);

        if (!File.Exists(localFilepath))
        {
            _logger?.LogError("File {LocalFilepath} to be uploaded not found, stopping upload", localFilepath);
            throw new FileNotFoundException("Could not find file for upload", localFilepath);
        }

        try
        {
            _logger?.LogDebug("Starting upload of {} to fileId {}", localFilepath, fileId);

            using var asyncCall = _grpcClient.Upload(headers, cancellationToken: cancellationToken);
            await asyncCall.RequestStream.WriteAsync(new FileUploadRequest { FileId = fileId }, cancellationToken).ConfigureAwait(false);

            await using FileStream fileStream = File.OpenRead(localFilepath);
            _logger?.LogTrace("Opened read filestream for {}", localFilepath);

            int chunkSizeBytes = Utils.ChunkSize;
            byte[] buffer = new byte[chunkSizeBytes];

            var chunk = new FileChunk();
            for (long i = 0; i < fileStream.Length; i += chunkSizeBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                chunk.Id++;
                if (fileStream.Position + chunkSizeBytes >= fileStream.Length)
                {
                    chunkSizeBytes = (int)(fileStream.Length - fileStream.Position);
                    buffer = new byte[chunkSizeBytes];
                    chunk.IsLast = true;
                    _logger?.LogTrace("Chunk {} is marked as last chunk", chunk.Id);
                }

                _logger?.LogTrace("Reading chunk {ChunkId}", chunk.Id);
                await fileStream.ReadExactlyAsync(buffer, 0, chunkSizeBytes, cancellationToken).ConfigureAwait(false);
                chunk.Content = UnsafeByteOperations.UnsafeWrap(buffer);

                await asyncCall.RequestStream.WriteAsync(new FileUploadRequest { FileChunk = chunk }, cancellationToken).ConfigureAwait(false);
                _logger?.LogTrace("Transferred chunk {}", chunk.Id);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await asyncCall.RequestStream.WriteAsync(new FileUploadRequest { GetMd5Hash = hashVerification }, cancellationToken).ConfigureAwait(false);

            await asyncCall.RequestStream.CompleteAsync().ConfigureAwait(false);
            var response = await asyncCall.ResponseAsync.ConfigureAwait(false);
            if (hashVerification)
            {
                string hash = Utils.CalculateMD5(localFilepath);
                _logger?.LogTrace("Calculated md5 hash {} for {}.", hash, localFilepath);
                string serverHash = response.Md5Hash;
                _logger?.LogTrace("Received md5 hash {} for fileId {} from server", serverHash, fileId);
                if (serverHash != hash)
                {

                    var ex = new CorruptedFileException(localFilepath, hash, serverHash, "");
                    _logger?.LogError(ex, "Hash comparison failed");
                    throw ex;
                }
            }

            _logger?.LogDebug("Finished upload of {} to fileId {}", localFilepath, fileId);
        }
        catch (RpcException rEx)
        {
            _logger?.LogError(rEx, "Upload of file {LocalFilepath} failed with gRPC exception", localFilepath);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Upload of file {LocalFilepath} failed with general exception", localFilepath);
            throw;
        }
    }

    private readonly InternalFileTransfer.InternalFileTransferClient _grpcClient;
    private readonly ILogger? _logger;
}
