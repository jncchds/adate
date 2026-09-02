namespace Game.Core.Content;

public interface ILocationCatalog
{
    LocationDefinition Get(string locationId);

    IReadOnlyList<LocationDefinition> All();
}
