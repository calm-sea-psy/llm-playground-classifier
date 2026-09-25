namespace Api.Shared.Experiments;

/// <summary>PUT /api/{모듈}/experiments/{id}/description 본문</summary>
public sealed record DescriptionRequest(string? Description);
