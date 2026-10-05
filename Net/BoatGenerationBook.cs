using System.Collections.Generic;

namespace SailwindCoop.Net
{
    public interface IBoatLayoutMessage
    {
        ushort LayoutBoat { get; }
        uint LayoutGeneration { get; set; }
    }
    public enum GenerationOrder { Past, Current, Future }
    /// <summary>Configuration order, independent of layout fingerprints or gameplay eligibility.</summary>
    public sealed class BoatGenerationBook
    {
        public static readonly BoatGenerationBook Session = new BoatGenerationBook();
        private readonly Dictionary<ushort, uint> _versions = new Dictionary<ushort, uint>();
        public uint Get(ushort boat) => _versions.TryGetValue(boat, out var value) ? value : 1;
        public uint Next(ushort boat)
        { uint value = Get(boat) + 1; if (value == 0) value = 1; _versions[boat] = value; return value; }
        public void Set(ushort boat, uint value) => _versions[boat] = value;
        public GenerationOrder Compare(ushort boat, uint value)
        {
            int delta = unchecked((int)(value - Get(boat)));
            return delta < 0 ? GenerationOrder.Past : delta > 0 ? GenerationOrder.Future : GenerationOrder.Current;
        }
        public void Clear() => _versions.Clear();
    }
}
