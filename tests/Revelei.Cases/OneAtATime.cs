using Xunit.Sdk;
using Xunit.v3;

// Everything in this assembly drives one desk; a second test's window must never open under the
// first's input, so nothing here runs side by side.
[assembly: Parallelization(Mode = ParallelMode.None)]
