# customer-360-hub durable MDM demo tape.
# Record with VHS (https://github.com/charmbracelet/vhs), from the repo root:
#   vhs samples/durable/customer-360-hub/assets/demo.tape
# Produces samples/durable/customer-360-hub/assets/demo.gif. Requires the .NET 10 SDK + pwsh.
#
# VHS renders reliably on Linux (see .github/workflows/demo-gif.yml); it is flaky on native
# Windows. Warm the build first (dotnet build src/Linkuity.Cli) so the run finishes inside
# the Sleep window below.

Output samples/durable/customer-360-hub/assets/demo.gif

Set Shell pwsh
Set FontSize 16
Set Width 1200
Set Height 800
Set Padding 20
Set TypingSpeed 40ms

Type "# Customer 360 hub: seed from a prior run, then evolve over three incremental ingests"
Enter
Sleep 700ms
Type "#  -> clean auto-join by exact phone, a canonical email change (golden v2), a review task"
Enter
Sleep 700ms

Type "pwsh -File scripts/Run-DurableScenario.ps1 -ScenarioPath samples/durable/customer-360-hub"
Enter
Sleep 30s
