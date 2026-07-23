# Company-resolution hero demo tape.
# Record with VHS (https://github.com/charmbracelet/vhs) from the repo root:
#   vhs showcases/company-resolution/assets/demo.tape
# Produces showcases/company-resolution/assets/demo.gif. Requires the .NET 10 SDK + pwsh.

Output showcases/company-resolution/assets/demo.gif

Set Shell pwsh
Set FontSize 18
Set Width 1200
Set Height 700
Set Padding 20
Set TypingSpeed 40ms

Type "# Resolve real companies across SEC EDGAR + GLEIF — no shared key"
Enter
Sleep 800ms

Type "./showcases/company-resolution/run-demo.ps1"
Enter
Sleep 30s
