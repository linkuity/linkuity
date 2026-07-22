# README assets

## Terminal demo

[`demo.tape`](demo.tape) is a [VHS](https://github.com/charmbracelet/vhs) script that
records the quick start — resolving the bundled 28-record sample into 10 golden records —
as an animated GIF.

**No GIF is committed yet.** It is intentionally not embedded in the root README as a
broken placeholder.

## Recommended: render in CI

VHS renders reliably on Linux but is flaky on native Windows (its headless-browser step
tends to hang). The [`Demo GIF` workflow](../../.github/workflows/demo-gif.yml) renders
the tape on an Ubuntu runner and commits `demo.gif` back to the branch. Once the workflow
is on the default branch, trigger it from the Actions tab, or from the CLI:

```bash
gh workflow run demo-gif.yml --ref <branch>
```

## Local rendering

To generate it locally instead:

1. Install VHS: `go install github.com/charmbracelet/vhs@latest` (or see the VHS README
   for other install methods; it also needs `ttyd` and `ffmpeg`).
2. From the repository root, run:

   ```bash
   vhs docs/assets/demo.tape
   ```

   This produces `docs/assets/demo.gif`.

3. Commit the GIF and reference it near the top of the root `README.md`, e.g.:

   ```markdown
   ![Linkuity resolving 28 records into 10 golden records](docs/assets/demo.gif)
   ```

If the first `dotnet` build makes the recording too long, either warm the build first
(`dotnet build`) and lower the `Sleep` in the tape, or record against a published
`Linkuity.Cli` binary (see [../cli.md](../cli.md#building-a-standalone-binary)).

An [asciinema](https://asciinema.org/) recording of the same commands is a fine
alternative; link the cast instead of embedding a GIF.
