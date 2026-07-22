# Linkuity quick-start demo tape.
#
# Record with VHS (https://github.com/charmbracelet/vhs), from the repo root:
#   vhs docs/assets/demo.tape        # produces docs/assets/demo.gif
#
# Requires the .NET 10 SDK on PATH. Adjust the Sleep after the run command if your
# first build takes longer than ~20s.

Output docs/assets/demo.gif

Set Shell bash
Set FontSize 18
Set Width 1200
Set Height 640
Set Padding 20
Set TypingSpeed 45ms

Type "# Resolve 28 messy source records into 10 golden records"
Enter
Sleep 800ms

Type "dotnet run --project src/Linkuity.Cli -- run \"
Enter
Type "  --input samples/people-multi-source/sample.csv \"
Enter
Type "  --config samples/people-multi-source/match-config.json \"
Enter
Type "  --output ./data/output/demo"
Enter
Sleep 500ms
Enter
Sleep 22s

Type "head -5 ./data/output/demo/golden-records.csv"
Enter
Sleep 2s

Type "echo golden records: $(( $(wc -l < ./data/output/demo/golden-records.csv) - 1 ))"
Enter
Sleep 3s
