namespace GubalLibrary.Tests;

// Localization changes the shared category cache. These tests must not run in parallel.
[CollectionDefinition("Localization")]
public sealed class LocalizationCollection;
