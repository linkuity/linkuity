namespace Linkuity.Mdm.Benchmarks;

/// <summary>Options controlling the shape of a synthetic dataset.</summary>
public record SyntheticDatasetOptions(
    int TotalRecords,
    int BatchSize,
    IReadOnlyList<string> Sources,
    double DuplicateRate,
    int Seed);

/// <summary>One batch of synthetic records assigned to a single source system.</summary>
public record SyntheticBatch(string Source, IReadOnlyList<SyntheticRecord> Records);

/// <summary>A single synthetic entity record with a stable source key and field bag.</summary>
public record SyntheticRecord(string SourceRecordId, IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// Generates deterministic synthetic datasets for the measurement harness.
/// A fixed <see cref="SyntheticDatasetOptions.Seed"/> produces identical output across calls.
/// </summary>
public class SyntheticDatasetGenerator
{
    private static readonly string[] FirstNames =
    [
        "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda",
        "William", "Barbara", "David", "Elizabeth", "Richard", "Susan", "Joseph", "Jessica",
        "Thomas", "Sarah", "Charles", "Karen", "Christopher", "Lisa", "Daniel", "Nancy",
        "Matthew", "Betty", "Anthony", "Margaret", "Mark", "Sandra"
    ];

    private static readonly string[] LastNames =
    [
        "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis",
        "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson",
        "Thomas", "Taylor", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson",
        "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson"
    ];

    private static readonly string[] Domains =
    [
        "gmail.com", "yahoo.com", "outlook.com", "hotmail.com", "icloud.com",
        "aol.com", "protonmail.com", "mail.com"
    ];

    /// <summary>
    /// Generates a list of <see cref="SyntheticBatch"/> objects from the given options.
    /// Records with the same email/name represent near-duplicates for matching exercises.
    /// </summary>
    public IReadOnlyList<SyntheticBatch> Generate(SyntheticDatasetOptions options)
    {
        var rng = new Random(options.Seed);

        // Track (name, email) pairs of already-generated records to enable duplication.
        var generated = new List<(string Name, string Email)>(options.TotalRecords);
        var records = new List<SyntheticRecord>(options.TotalRecords);

        for (var i = 0; i < options.TotalRecords; i++)
        {
            string name, email;

            if (generated.Count > 0 && rng.NextDouble() < options.DuplicateRate)
            {
                // Near-duplicate: reuse name + email from a randomly chosen earlier record.
                var prior = generated[rng.Next(generated.Count)];
                name = prior.Name;
                email = prior.Email;
            }
            else
            {
                var firstName = FirstNames[rng.Next(FirstNames.Length)];
                var lastName = LastNames[rng.Next(LastNames.Length)];
                name = $"{firstName} {lastName}";
                var domain = Domains[rng.Next(Domains.Length)];
                var suffix = rng.Next(1000);
                email = $"{firstName.ToLowerInvariant()}.{lastName.ToLowerInvariant()}{suffix}@{domain}";
            }

            generated.Add((name, email));

            var batchIndex = i / options.BatchSize;
            var source = options.Sources[batchIndex % options.Sources.Count];
            var sourceRecordId = $"rec-{i:D6}";
            var phone = $"({rng.Next(200, 1000)}) {rng.Next(100, 1000)}-{rng.Next(1000, 10000)}";

            records.Add(new SyntheticRecord(sourceRecordId, new Dictionary<string, string>
            {
                ["id"] = sourceRecordId,
                ["source"] = source,
                ["name"] = name,
                ["email"] = email,
                ["phone"] = phone,
            }));
        }

        // Slice the flat record list into batches of BatchSize, assigning Sources round-robin.
        var batches = new List<SyntheticBatch>();
        for (var b = 0; b * options.BatchSize < options.TotalRecords; b++)
        {
            var start = b * options.BatchSize;
            var count = Math.Min(options.BatchSize, options.TotalRecords - start);
            var source = options.Sources[b % options.Sources.Count];
            batches.Add(new SyntheticBatch(source, records.GetRange(start, count)));
        }

        return batches;
    }
}
