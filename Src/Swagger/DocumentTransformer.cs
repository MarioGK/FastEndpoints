using System.Net.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace FastEndpoints.Swagger;

sealed class DocumentTransformer : IOpenApiDocumentTransformer
{
    readonly int _maxEpVer;
    readonly int _minEpVer;
    readonly int _docRelVer;
    readonly bool _showDeprecated;

    public DocumentTransformer(int minEndpointVersion, int maxEndpointVersion, int releaseVersion, bool showDeprecatedOps)
    {
        _minEpVer = minEndpointVersion;
        _maxEpVer = maxEndpointVersion;
        _docRelVer = releaseVersion;
        _showDeprecated = showDeprecatedOps;

        switch (_docRelVer)
        {
            case > 0 when _minEpVer > 0:
                throw new NotSupportedException(
                    $"'{nameof(DocumentOptions.MinEndpointVersion)}' cannot be used together with '{nameof(DocumentOptions.ReleaseVersion)}'." +
                    $" Please choose a single strategy when defining a swagger document!");
            case > 0 when _maxEpVer > 0:
                throw new NotSupportedException(
                    $"'{nameof(DocumentOptions.MaxEndpointVersion)}' cannot be used together with '{nameof(DocumentOptions.ReleaseVersion)}'. " +
                    $"Please choose a single strategy when defining a swagger document");
        }

        if (_maxEpVer < _minEpVer)
            throw new ArgumentException($"{nameof(maxEndpointVersion)} must be greater than or equal to {nameof(minEndpointVersion)}");
    }

    static readonly string _isLatest = "__isLatest__";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var pathItems = document.Paths
                                .SelectMany(p => p.Value.Operations)
                                .Select(
                                    o =>
                                    {
                                        var tagSegments = o.Value.Tags.SingleOrDefault(t => t.Name.StartsWith("|"))?.Name.Split("|");

                                        return new
                                        {
                                            isFastEp = tagSegments?.Length > 0,
                                            route = tagSegments?[1],
                                            epVer = Convert.ToInt32(tagSegments?[2]),
                                            startingRelVer = Convert.ToInt32(tagSegments?[3]),
                                            depVer = Convert.ToInt32(tagSegments?[4]),
                                            pathItm = o,
                                            parentPath = document.Paths.FirstOrDefault(p => p.Value.Operations.Contains(o))
                                        };
                                    })
                                .GroupBy(x => x.route)
                                .Select(
                                    g =>
                                    {
                                        var sortedGroup = g.OrderByDescending(x => x.epVer);
                                        var latestVersion = sortedGroup.FirstOrDefault(x => x.startingRelVer <= _docRelVer)?.epVer ?? 0;

                                        return new
                                        {
                                            pathKeys = sortedGroup
                                                       .Where(
                                                           x => _docRelVer > 0
                                                                    ? x.startingRelVer <= _docRelVer
                                                                    : x.epVer >= _minEpVer && x.epVer <= _maxEpVer)
                                                       .Select(
                                                           x =>
                                                           {
                                                               if (x.isFastEp && x.epVer == latestVersion)
                                                               {
                                                                   x.pathItm.Value.Extensions ??= new Dictionary<string, IOpenApiExtension>();
                                                               }

                                                               return x;
                                                           })
                                                       .Take(_showDeprecated ? g.Count() : 1)
                                                       .Where(x => x.depVer == 0 || _showDeprecated || x.depVer > _maxEpVer)
                                                       .Select(x => x.parentPath.Key)
                                        };
                                    })
                                .SelectMany(x => x.pathKeys)
                                .ToHashSet();

        var pathsToRemove = new List<string>();

        foreach (var p in document.Paths)
        {
            var isFastEp = p.Value.Operations.Any(o => o.Value.Tags.Any(t => t.Name.StartsWith('|')));

            if (!isFastEp)
                continue;

            if (!pathItems.Contains(p.Key))
                pathsToRemove.Add(p.Key);

            var opsToRemove = new List<HttpMethod>();

            foreach (var op in p.Value.Operations)
            {
                var tagSegments = op.Value.Tags.SingleOrDefault(t => t.Name.StartsWith('|'))?.Name.Split('|');
                var depVer = Convert.ToInt32(tagSegments?[4]);

                var isDeprecated = _docRelVer > 0
                                       ? depVer > 0 && _docRelVer >= depVer
                                       : _maxEpVer >= depVer && depVer != 0;

                if (isDeprecated && _showDeprecated)
                    op.Value.Deprecated = true;

                if (isDeprecated && !_showDeprecated)
                    opsToRemove.Add(op.Key);

                var metaTag = op.Value.Tags.SingleOrDefault(t => t.Name.StartsWith('|'));
                if (metaTag is not null)
                    op.Value.Tags.Remove(metaTag);
            }

            foreach (var opKey in opsToRemove)
                p.Value.Operations.Remove(opKey);
        }

        foreach (var key in pathsToRemove)
            document.Paths.Remove(key);

        return Task.CompletedTask;
    }

    public void Dispose() { }
}
