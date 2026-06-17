namespace Starling.Dom;

public sealed class DocumentFragment : Node
{
    public override NodeKind Kind => NodeKind.DocumentFragment;

    public override string NodeName => "#document-fragment";

    /// <summary>The descendant element with the given id, in tree order, or null.
    /// DOM §4.5 <c>getElementById</c>.</summary>
    public Element? GetElementById(string elementId)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        foreach (var node in Descendants())
        {
            if (node is Element element && element.Id == elementId)
            {
                return element;
            }
        }

        return null;
    }
}
