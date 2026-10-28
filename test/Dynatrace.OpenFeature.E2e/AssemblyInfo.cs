using Xunit;

// every scenario drives the one shared mock server, so features must not run concurrently
[assembly: CollectionBehavior(DisableTestParallelization = true)]
