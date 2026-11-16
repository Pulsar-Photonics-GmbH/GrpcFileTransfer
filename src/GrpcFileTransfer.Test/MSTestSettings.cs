// All tests share one in-process gRPC server and its token handler (see SharedGrpcServer),
// so they run one after another.
[assembly: DoNotParallelize]
