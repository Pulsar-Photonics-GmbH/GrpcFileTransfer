using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Pulsar.GrpcFileTransfer.ServiceInterface;
using Enum = System.Enum;

namespace Pulsar.GrpcFileTransfer.Service;

public abstract class AbstractFileTransferService : FileTransfer.FileTransferBase
{
    protected AbstractFileTransferService()
    {

    }

    protected AbstractFileTransferService(ILogger logger)
    {
        _logger = logger;
    }

    public sealed override Task<ServiceInformation> GetInfo(Empty request, ServerCallContext context)
    {
        try
        {
            return GetInfoImplementation(request, context);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in GetInfo call");
            if (ex is RpcException)
                throw;
            throw new RpcException(new Status(StatusCode.Internal, ex.Message), ex.Message);
        }
    }

    /// <summary>
    /// Provides basic information about the service, such as name, description and version.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    protected abstract Task<ServiceInformation> GetInfoImplementation(Empty request, ServerCallContext context);

    public sealed override async Task<FileUploadResponse> Upload(IAsyncStreamReader<FileUploadRequest> requestStream, ServerCallContext context)
    {
        try
        {
            _logger?.LogTrace($"Starting upload");

            if (!ValidateHeaders(context.RequestHeaders, out string authorizationMessage))
                throw new RpcException(new Status(StatusCode.Unauthenticated, $"Authorization failed with message: {authorizationMessage}"));
            string? md5hash = null;
            FileStream? fileStream = null;
            bool uploadSuccess = false;
            string filepath = string.Empty;
            string fileID = string.Empty;
            try
            {
                FileUploadRequest.RequestOneofCase lastRequestCase;
                if (await requestStream.MoveNext().ConfigureAwait(false))
                {
                    if (requestStream.Current.RequestCase != FileUploadRequest.RequestOneofCase.FileId)
                        throw new RpcException(new Status(StatusCode.InvalidArgument, $"First request in stream must be {nameof(FileUploadRequest.RequestOneofCase.FileId)}"));
                    fileID = requestStream.Current.FileId;

                    if (fileID.Trim() == string.Empty)
                        throw new RpcException(new Status(StatusCode.InvalidArgument, $"{FileUploadRequest.RequestOneofCase.FileId} must not be empty;"));

                    if (!PreUploadHook(fileID, out filepath, out string preHookMessage))
                        throw new RpcException(new Status(StatusCode.FailedPrecondition, $"{nameof(PreUploadHook)} failed with message:\n{preHookMessage}"));

                    var directoryInfo = new FileInfo(filepath).Directory;
                    if (directoryInfo != null)
                    {
                        var parentFolder = directoryInfo.FullName;
                        if (!Directory.Exists(parentFolder))
                            Directory.CreateDirectory(parentFolder);
                    }
                    else
                    {
                        throw new RpcException(new Status(StatusCode.InvalidArgument, $"The path {filepath} is invalid"));
                    }

                    fileStream = File.OpenWrite(filepath);
                    lastRequestCase = requestStream.Current.RequestCase;
                }
                else
                    throw new RpcException(new Status(StatusCode.Aborted, "Cancelled by client"));

                while (await requestStream.MoveNext().ConfigureAwait(false))
                {
                    switch (requestStream.Current.RequestCase)
                    {
                        case FileUploadRequest.RequestOneofCase.None:
                        case FileUploadRequest.RequestOneofCase.FileId:
                            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                "Request type during transfer must either be File or VerifyData"));
                        case FileUploadRequest.RequestOneofCase.FileChunk:
                            if (fileStream is not { CanWrite: true })
                                if (lastRequestCase == FileUploadRequest.RequestOneofCase.GetMd5Hash)
                                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                                        "Request with VerifyData finishes file transfer, no further content transfer possible"));
                                else
                                    throw new RpcException(new Status(StatusCode.Internal, "File not writable"));

                            lastRequestCase = requestStream.Current.RequestCase;
                            await fileStream.WriteAsync(requestStream.Current.FileChunk.Content.Memory)
                                .ConfigureAwait(false);
                            if (requestStream.Current.FileChunk.IsLast)
                            {
                                await fileStream.DisposeAsync().ConfigureAwait(false);
                                fileStream = null;
                                uploadSuccess = true;
                            }

                            break;
                        case FileUploadRequest.RequestOneofCase.GetMd5Hash:
                            lastRequestCase = requestStream.Current.RequestCase;
                            if (fileStream != null)
                                await fileStream.DisposeAsync().ConfigureAwait(false);
                            md5hash = Utils.CalculateMD5(filepath);
                            break;
                        default:
                            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                $"Unknown request case {Enum.GetName(requestStream.Current.RequestCase)}"));
                    }
                }

                if (fileStream != null)
                    await fileStream.DisposeAsync().ConfigureAwait(false);
                _logger?.LogTrace("stream closed");
            }
            finally
            {
                if (fileStream != null)
                    await fileStream.DisposeAsync().ConfigureAwait(false);

                uploadSuccess = PostUploadHook(fileID, uploadSuccess, out string postHookMessage);

                if (!uploadSuccess)
                {
                    if (File.Exists(filepath))
                        File.Delete(filepath);
                    var message = "Upload failed!";
                    if (postHookMessage.Trim() != string.Empty)
                        message += $"\n{nameof(PostUploadHook)} failed with message:\n{postHookMessage}";
                    throw new RpcException(new Status(StatusCode.Aborted, message));
                }
            }

            return new FileUploadResponse { Md5Hash = md5hash };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error while uploading");
            if (ex is RpcException)
                throw;
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    /// <summary>
    /// Perform server-side actions before a upload is started.
    /// Can be used e.g. to match the <see cref="fileID"/> to a filepath using a database.
    /// Default behavior: sets empty message and returns <see cref="fileID"/> as <see cref="filePath"/>
    /// </summary>
    /// <param name="fileID">Identifier for the file to be uploaded</param>
    /// <param name="filePath">Target path of the file to be uploaded on the server</param>
    /// /// <param name="message">If false is returned, an <see cref="RpcException"/> will be transmitted to the client containing this message.</param>
    /// <returns>True if upload is allowed, false if it is not</returns>
    protected virtual bool PreUploadHook(string fileID, out string filePath, out string message)
    {
        filePath = fileID;
        message = string.Empty;
        return true;
    }

    /// <summary>
    /// Perform server-side actions after the upload is finished / has failed.
    /// Can be used for e.g. for cleanup tasks.
    /// Default behavior: sets empty message and returns <see cref="uploadSuccessful"/>
    /// </summary>
    /// <param name="fileID">fileID that was uploaded</param>
    /// <param name="message">If false is returned, an <see cref="RpcException"/> will be transmitted to the client containing this message.</param>
    /// <param name="uploadSuccessful">If the upload operation was successful</param>
    /// <returns>Return false if upload should be handled as failed.</returns>
    protected virtual bool PostUploadHook(string fileID, bool uploadSuccessful, out string message)
    {
        message = string.Empty;
        return uploadSuccessful;
    }

    public sealed override async Task Download(FileDownloadRequest request, IServerStreamWriter<FileDownloadResponse> responseStream, ServerCallContext context)
    {
        try
        {
            if (!ValidateHeaders(context.RequestHeaders, out string authorizationMessage))
                throw new RpcException(new Status(StatusCode.Unauthenticated, $"Authorization failed with message: {authorizationMessage}"));

            if (!PreDownloadHook(request.FileID, out string filepath, out string preHookMessage))
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"{nameof(PreDownloadHook)} failed with message:\n{preHookMessage}"));

            if (!File.Exists(filepath))
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"File {filepath} does not exist."));

            if (request.GetMd5Hash)
                await responseStream.WriteAsync(new FileDownloadResponse { Md5Hash = Utils.CalculateMD5(filepath) }).ConfigureAwait(false);

            await using var fileStream = File.OpenRead(filepath);

            int chunkSizeBytes = Utils.ChunkSize;
            byte[] buffer = new byte[Utils.ChunkSize];

            var chunk = new FileChunk();
            bool downloadSuccess = false;

            for (long i = 0; i < fileStream.Length; i += chunkSizeBytes)
            {
                if (fileStream.Position + chunkSizeBytes >= fileStream.Length)
                {
                    chunkSizeBytes = (int)(fileStream.Length - fileStream.Position);
                    buffer = new byte[chunkSizeBytes];
                    chunk.IsLast = true;
                }

                await fileStream.ReadExactlyAsync(buffer, 0, chunkSizeBytes).ConfigureAwait(false);

                chunk.Content = UnsafeByteOperations.UnsafeWrap(buffer);
                chunk.Id++;

                await responseStream.WriteAsync(new FileDownloadResponse { FileChunk = chunk }).ConfigureAwait(false);
                if (chunk.IsLast)
                    downloadSuccess = true;
            }

            downloadSuccess = PostDownloadHook(request.FileID, downloadSuccess, out string postHookMessage);

            if (!downloadSuccess)
            {
                var message = "Download failed!";
                if (postHookMessage.Trim() != string.Empty)
                    message += $"\n{nameof(PostDownloadHook)} failed with message:\n{postHookMessage}";
                throw new RpcException(new Status(StatusCode.Aborted, message));
            }
        }
        catch (Exception ex)
        {
            if (ex is RpcException)
                throw;
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    /// <summary>
    /// Perform server-side actions before a download is started.
    /// Can be used e.g. to match the <see cref="fileID"/> to a filepath using a database.
    /// Default behavior: sets empty message and returns <see cref="fileID"/> as <see cref="filePath"/>
    /// </summary>
    /// <param name="fileID">Identifier for the file to be downloaded</param>
    /// <param name="filePath">Path of the file to download</param>
    /// <param name="message">If false is returned, an <see cref="RpcException"/> will be transmitted to the client containing this message.</param>
    /// <returns>True if upload is allowed, false if it is not</returns>
    protected virtual bool PreDownloadHook(string fileID, out string filePath, out string message)
    {
        filePath = fileID;
        message = string.Empty;
        return true;
    }

    /// <summary>
    /// Perform server-side actions after the download is finished / has failed.
    /// Can be used for e.g. for cleanup tasks.
    /// Default behavior: sets empty message and returns <see cref="downloadSuccessful"/>
    /// </summary>
    /// <param name="fileID">fileID that was downloaded</param>
    /// <param name="message">If false is returned, an <see cref="RpcException"/> will be transmitted to the client containing this message.</param>
    /// <param name="downloadSuccessful">If the download operation was successful</param>
    /// <returns>Return false if download should be handled as failed.</returns>
    protected virtual bool PostDownloadHook(string fileID, bool downloadSuccessful, out string message)
    {
        message = string.Empty;
        return downloadSuccessful;
    }

    /// <summary>
    /// Called before every up- or download. Can be used for custom header validation.
    /// Default behavior: return true without any validation.
    /// </summary>
    /// <param name="headers">Headers provided by the client.</param>
    /// <param name="message">If false is returned, the content of <see cref="message"/> will be transmitted in a <see cref="RpcException"/> to the client.</param>
    /// <returns></returns>
    protected virtual bool ValidateHeaders(Metadata headers, out string message)
    {
        message = string.Empty;
        return true;
    }

    protected readonly ILogger? _logger;
}