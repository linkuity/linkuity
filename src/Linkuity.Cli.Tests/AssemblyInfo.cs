using Xunit;

// These tests redirect the process-global Console.Out to capture CLI stdout.
// Console.Out is shared mutable state, so running test collections in parallel
// races (a class disposing its StringWriter while another writes throws
// ObjectDisposedException). Serialize the assembly's collections to make the
// suite deterministic; the tests are fast, so the cost is negligible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
