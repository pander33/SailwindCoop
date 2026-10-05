using System;
using LiteNetLib.Utils;
namespace SailwindCoop.Net
{
    /// <summary>Typed recipe/component state. No Unity object references travel on the wire.</summary>
    public sealed class ItemDetails : IEquatable<ItemDetails>
    {
        public bool HasFood;
        public float Dried;
        public float Smoked;
        public float Salted;
        public float Spoiled;
        public bool InWater;
        public bool HasCookable;
        public float CookHeat;
        public int CookStoveId;
        public int CookSlot = -1;
        public byte RecipeKind;
        public float Water;
        public float Energy;
        public float UncookedEnergy;
        public float Vitamins;
        public float Protein;
        public float SoupSpoiled;
        public float SoupSalted;
        public float TeaAmount;
        public float CookedTeaAmount;
        public int TeaType;
        public bool HasStove;
        public float StoveHeat;
        public bool HasFuel;
        public bool FuelLit;
        public bool FuelInserted;
        public int FuelStoveId;
        public bool HasPipe;
        public float PipeHeat;
        public bool HasClock, ClockOpen, HasQuadrant, QuadrantInspect, HasScroll, ScrollOpen, HasTotem, TotemSpent;
        public int ScrollPage;
        public void Serialize(NetDataWriter w)
        {
            w.Put(HasFood);
            w.Put(Dried);
            w.Put(Smoked);
            w.Put(Salted);
            w.Put(Spoiled);
            w.Put(InWater);
            w.Put(HasCookable);
            w.Put(CookHeat);
            w.Put(CookStoveId);
            w.Put(CookSlot);
            w.Put(RecipeKind);
            w.Put(Water);
            w.Put(Energy);
            w.Put(UncookedEnergy);
            w.Put(Vitamins);
            w.Put(Protein);
            w.Put(SoupSpoiled);
            w.Put(SoupSalted);
            w.Put(TeaAmount);
            w.Put(CookedTeaAmount);
            w.Put(TeaType);
            w.Put(HasStove);
            w.Put(StoveHeat);
            w.Put(HasFuel);
            w.Put(FuelLit);
            w.Put(FuelInserted);
            w.Put(FuelStoveId);
            w.Put(HasPipe);
            w.Put(PipeHeat);
            w.Put(HasClock); w.Put(ClockOpen); w.Put(HasQuadrant); w.Put(QuadrantInspect);
            w.Put(HasScroll); w.Put(ScrollOpen); w.Put(ScrollPage); w.Put(HasTotem); w.Put(TotemSpent);
        }
        public void Deserialize(NetDataReader r)
        {
            HasFood = r.GetBool();
            Dried = r.GetFloat();
            Smoked = r.GetFloat();
            Salted = r.GetFloat();
            Spoiled = r.GetFloat();
            InWater = r.GetBool();
            HasCookable = r.GetBool();
            CookHeat = r.GetFloat();
            CookStoveId = r.GetInt();
            CookSlot = r.GetInt();
            RecipeKind = r.GetByte();
            Water = r.GetFloat();
            Energy = r.GetFloat();
            UncookedEnergy = r.GetFloat();
            Vitamins = r.GetFloat();
            Protein = r.GetFloat();
            SoupSpoiled = r.GetFloat();
            SoupSalted = r.GetFloat();
            TeaAmount = r.GetFloat();
            CookedTeaAmount = r.GetFloat();
            TeaType = r.GetInt();
            HasStove = r.GetBool();
            StoveHeat = r.GetFloat();
            HasFuel = r.GetBool();
            FuelLit = r.GetBool();
            FuelInserted = r.GetBool();
            FuelStoveId = r.GetInt();
            HasPipe = r.GetBool();
            PipeHeat = r.GetFloat();
            HasClock = r.GetBool(); ClockOpen = r.GetBool(); HasQuadrant = r.GetBool(); QuadrantInspect = r.GetBool();
            HasScroll = r.GetBool(); ScrollOpen = r.GetBool(); ScrollPage = r.GetInt(); HasTotem = r.GetBool(); TotemSpent = r.GetBool();
        }
        public bool Equals(ItemDetails other) => other != null &&
            HasFood.Equals(other.HasFood) &&
            Dried.Equals(other.Dried) &&
            Smoked.Equals(other.Smoked) &&
            Salted.Equals(other.Salted) &&
            Spoiled.Equals(other.Spoiled) &&
            InWater.Equals(other.InWater) &&
            HasCookable.Equals(other.HasCookable) &&
            CookHeat.Equals(other.CookHeat) &&
            CookStoveId.Equals(other.CookStoveId) &&
            CookSlot.Equals(other.CookSlot) &&
            RecipeKind.Equals(other.RecipeKind) &&
            Water.Equals(other.Water) &&
            Energy.Equals(other.Energy) &&
            UncookedEnergy.Equals(other.UncookedEnergy) &&
            Vitamins.Equals(other.Vitamins) &&
            Protein.Equals(other.Protein) &&
            SoupSpoiled.Equals(other.SoupSpoiled) &&
            SoupSalted.Equals(other.SoupSalted) &&
            TeaAmount.Equals(other.TeaAmount) &&
            CookedTeaAmount.Equals(other.CookedTeaAmount) &&
            TeaType.Equals(other.TeaType) &&
            HasStove.Equals(other.HasStove) &&
            StoveHeat.Equals(other.StoveHeat) &&
            HasFuel.Equals(other.HasFuel) &&
            FuelLit.Equals(other.FuelLit) &&
            FuelInserted.Equals(other.FuelInserted) &&
            FuelStoveId.Equals(other.FuelStoveId) &&
            HasPipe.Equals(other.HasPipe) &&
            PipeHeat.Equals(other.PipeHeat) && HasClock == other.HasClock && ClockOpen == other.ClockOpen &&
            HasQuadrant == other.HasQuadrant && QuadrantInspect == other.QuadrantInspect && HasScroll == other.HasScroll &&
            ScrollOpen == other.ScrollOpen && ScrollPage == other.ScrollPage && HasTotem == other.HasTotem && TotemSpent == other.TotemSpent;
    }
}
