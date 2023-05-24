using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Pulsar.GrpcFileTransfer.ServiceInterface;
using Enum = System.Enum;

namespace Pulsar.GrpcFileTransfer.Service;

public abstract class AbstractFileTransferService : InternalFileTransfer.InternalFileTransferBase
{
    protected AbstractFileTransferService()
    {
    }

    protected AbstractFileTransferService(ILogger logger)
    {
        _logger = logger;
    }

    public override Task<ServiceInformation> GetInfo(Empty request, ServerCallContext context)
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

    public override async Task<FileUploadResponse> Upload(IAsyncStreamReader<FileUploadRequest> requestStream, ServerCallContext context)
    {
        try
        {
            _logger?.LogTrace($"Starting upload");

            var validationResult = await ValidateHeaders(context.RequestHeaders).ConfigureAwait(false);
            if (!validationResult.ValidationSuccessful)
                throw new RpcException(new Status(StatusCode.Unauthenticated, $"Authorization failed with message: {validationResult.Message}"));
            string? md5hash = null;
            FileStream? fileStream = null;
            bool uploadSuccess = false;
            string filepath = string.Empty;
            string fileId = string.Empty;
            try
            {
                FileUploadRequest.RequestOneofCase lastRequestCase;
                if (await requestStream.MoveNext().ConfigureAwait(false))
                {
                    if (requestStream.Current.RequestCase != FileUploadRequest.RequestOneofCase.FileId)
                        throw new RpcException(new Status(StatusCode.InvalidArgument, $"First request in stream must be {nameof(FileUploadRequest.RequestOneofCase.FileId)}"));
                    fileId = requestStream.Current.FileId;

                    if (fileId.Trim() == string.Empty)
                        throw new RpcException(new Status(StatusCode.InvalidArgument, $"{FileUploadRequest.RequestOneofCase.FileId} must not be empty;"));

                    var preUploadResult = await PreUploadHook(fileId).ConfigureAwait(false);
                    if (!preUploadResult.DoAction)
                        throw new RpcException(new Status(StatusCode.FailedPrecondition, $"{nameof(PreUploadHook)} failed with message:\n{preUploadResult.Message}"));
                    filepath = preUploadResult.FilePath;

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

                var successBefore = uploadSuccess;
                var postHookResult = await PostUploadHook(fileId, uploadSuccess).ConfigureAwait(false);

                uploadSuccess = postHookResult.ActionSuccessful;
                if (!uploadSuccess)
                {
                    if (File.Exists(filepath))
                        File.Delete(filepath);
                    if (successBefore)
                    {
                        var message = "Upload failed!";
                        if (postHookResult.Message.Trim() != string.Empty)
                            message += $"\n{nameof(PostUploadHook)} failed with message:\n{postHookResult.Message}";
                        throw new RpcException(new Status(StatusCode.Aborted, message));
                    }
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
    /// Can be used e.g. to match the <see cref="fileId"/> to a filepath using a database.
    /// Default behavior: sets <see cref="PreHookResponse.FilePath"/> = <see cref="fileId"/> and continues the upload.
    /// </summary>
    /// <param name="fileId">Identifier for the file to be uploaded</param>
    /// <returns></returns>
    protected virtual Task<PreHookResponse> PreUploadHook(string fileId)
    {
        _logger?.LogTrace("Using default pre upload hook");
        return Task.FromResult(new PreHookResponse(true, fileId, string.Empty));
    }


    /// <summary>
    /// Perform server-side actions after the upload is finished / has failed.
    /// Can be used for e.g. for cleanup tasks.
    /// Default behavior: sets empty message and returns <see cref="uploadSuccessful"/>
    /// </summary>
    /// <param name="fileId">fileId that was uploaded</param>
    /// <param name="uploadSuccessful">If the upload operation was successful</param>
    /// <returns></returns>
    protected virtual Task<PostHookResponse> PostUploadHook(string fileId, bool uploadSuccessful)
    {
        _logger?.LogTrace("Using default post upload hook");
        return Task.FromResult(new PostHookResponse(uploadSuccessful, string.Empty));
    }

    public override async Task Download(FileDownloadRequest request, IServerStreamWriter<FileDownloadResponse> responseStream, ServerCallContext context)
    {
        try
        {
            bool downloadSuccess = false;
            try
            {
                var validationResult = await ValidateHeaders(context.RequestHeaders).ConfigureAwait(false);
                if (!validationResult.ValidationSuccessful)
                    throw new RpcException(new Status(StatusCode.Unauthenticated, $"Authorization failed with message: {validationResult.Message}"));

                var preHookResult = await PreDownloadHook(request.FileId).ConfigureAwait(false);
                if (!preHookResult.DoAction)
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, $"{nameof(PreDownloadHook)} failed with message:\n{preHookResult.Message}"));

                var filepath = preHookResult.FilePath;
                if (!File.Exists(filepath))
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"File {filepath} does not exist."));

                if (request.GetMd5Hash)
                    await responseStream.WriteAsync(new FileDownloadResponse { Md5Hash = Utils.CalculateMD5(filepath) }).ConfigureAwait(false);

                await using var fileStream = File.OpenRead(filepath);

                int chunkSizeBytes = Utils.ChunkSize;
                byte[] buffer = new byte[Utils.ChunkSize];

                var chunk = new FileChunk();
                while (!chunk.IsLast)
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
                }

                downloadSuccess = true;
            }
            finally
            {
                var successBefore = downloadSuccess;
                var postHookResult = await PostDownloadHook(request.FileId, downloadSuccess).ConfigureAwait(false);
                downloadSuccess = postHookResult.ActionSuccessful;

                if (successBefore && !downloadSuccess)
                {
                    var message = "Download failed!";
                    if (postHookResult.Message.Trim() != string.Empty)
                        message += $"\n{nameof(PostDownloadHook)} failed with message:\n{postHookResult.Message}";
                    throw new RpcException(new Status(StatusCode.Aborted, message));
                }
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
    /// Can be used e.g. to match the <see cref="fileId"/> to a filepath using a database.
    /// Default behavior: sets <see cref="PreHookResponse.FilePath"/> = <see cref="fileId"/> and continues the download.
    /// </summary>
    /// <param name="fileId">Identifier for the file to be downloaded</param>
    /// <returns></returns>
    protected virtual Task<PreHookResponse> PreDownloadHook(string fileId)
    {
        _logger?.LogTrace("Using default pre download hook");
        return Task.FromResult(new PreHookResponse(true, fileId, string.Empty));
    }

    /// <summary>
    /// Perform server-side actions after the download is finished / has failed.
    /// Can be used for e.g. for cleanup tasks.
    /// Default behavior: sets empty message and returns <see cref="downloadSuccessful"/>
    /// </summary>
    /// <param name="fileId">Identifier for the file that was downloaded</param>
    /// <param name="downloadSuccessful">If the download operation was successful</param>
    /// <returns></returns>
    protected virtual Task<PostHookResponse> PostDownloadHook(string fileId, bool downloadSuccessful)
    {
        _logger?.LogTrace("Using default post download hook");
        return Task.FromResult(new PostHookResponse(downloadSuccessful, string.Empty));
    }

    /// <summary>
    /// Called before every up- or download. Can be used for custom header validation.
    /// Default behavior: return success without any validation.
    /// </summary>
    /// <param name="headers">Headers provided by the client.</param>
    /// <returns></returns>
    protected virtual Task<ValidationResult> ValidateHeaders(Metadata headers)
    {
        _logger?.LogTrace("Using default header validation");
        return Task.FromResult(new ValidationResult(true, string.Empty));
    }

    protected readonly ILogger? _logger;
}

/// <summary>
///
/// </summary>
/// <param name="DoAction">If the up / download action should be carried out.</param>
/// <param name="FilePath">Path of the file to up / download on the server.</param>
/// <param name="Message">If false is returned, the content of <see cref="Message"/> will be transmitted in a <see cref="RpcException"/> to the client.</param>
public record PreHookResponse(bool DoAction, string FilePath, string Message);

/// <summary>
///
/// </summary>
/// <param name="ActionSuccessful">If up / download action was successful</param>
/// <param name="Message">If false is returned, the content of <see cref="Message"/> will be transmitted in a <see cref="RpcException"/> to the client.</param>
public record PostHookResponse(bool ActionSuccessful, string Message);

/// <summary>
///
/// </summary>
/// <param name="ValidationSuccessful">If the validation was successful</param>
/// <param name="Message">If false is returned, the content of <see cref="Message"/> will be transmitted in a <see cref="RpcException"/> to the client.</param>
public record ValidationResult(bool ValidationSuccessful, string Message);