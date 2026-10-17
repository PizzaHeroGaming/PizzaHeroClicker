using Xunit;

// Several tests run a game in real time against the engine (clicks landing on moving targets, a bar
// steered round a circle) and judge how well it did. Sharing the CPU with other tests running at the
// same moment makes those results depend on how busy the machine is, so tests run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
