using System.Text.Json.Serialization;

namespace Mergician.Entities;

public record GitLabFileDiff(
    [property: JsonPropertyName("new_path")]
    string NewPath);

public record GitLabCompareResult(
    [property: JsonPropertyName("diffs")] List<GitLabFileDiff> Diffs,
    [property: JsonPropertyName("compare_timeout")]
    bool CompareTimeout,
    [property: JsonPropertyName("compare_same_ref")]
    bool CompareSameRef);