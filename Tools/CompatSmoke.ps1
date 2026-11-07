# Exercises production market/finance sync logic with an in-memory game and transport.
# Unity physics and EO's own Harmony patches still require in-game acceptance.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stub = @'
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using LiteNetLib;
using SailwindCoop.Net;
using SailwindCoop.Sync;
using UnityEngine;
namespace UnityEngine {
 public static class Time { public static float unscaledTime; }
 public class Collider { public ShipItem Item; public T GetComponent<T>() where T:class=>Item as T; }
 public class Transform { public PurchasableBoat Purchase; public T GetComponentInParent<T>() where T:class=>Purchase as T; public bool IsChildOf(Transform root)=>ReferenceEquals(this,root); }
}
public class PurchasableBoat { public SaveableObject Save; public T GetComponent<T>() where T:class=>Save as T; }
public class SaveableObject { public bool extraSetting; }
public class Port { public static Port[] ports; public static int Lookups; public IslandMarket Market; public T GetComponent<T>() where T:class { Lookups++; return Market as T; } }
public class IslandMarket { public float[] currentSupply; public float goodsSoftCapOverride,supplyPurchaseLimit; public PriceReport[] knownPrices; }
public class PriceReport { public bool approved; public int day; }
public class DebugMarketTracker { public static DebugMarketTracker instance=new DebugMarketTracker(); public static float goodsAmountSoftCap=100; public float positivePriceMult=1,negativePriceMult=1; }
public class ShipItem { public SaveablePrefab Save; public T GetComponent<T>() where T:class => Save as T; }
public class ShipItemCrate : ShipItem {}
public class Good { public ShipItem Item; public T GetComponent<T>() where T:class => Item as T; }
public static class ShipItemRegistry { public static IEnumerable<ShipItem> Active()=>new ShipItem[0]; }
public class SaveablePrefab { public int instanceId; }
public static class GameState { public static bool playing=true, currentlyLoading; public static int day=5; }
namespace LiteNetLib {
 public enum DeliveryMethod { ReliableOrdered }
 public class NetPeer { public List<object> Sent=new List<object>(); public void Send(object m,DeliveryMethod d){Sent.Add(m);} }
}
namespace SailwindCoop.Runtime { public class CoopLog { public struct Repeat {} } }
namespace SailwindCoop {
 public static class Plugin { public static Log Logger=new Log(); }
 public class Log { public void ReportError(string s,Exception e,ref Runtime.CoopLog.Repeat r){throw e;} public void LogWarning(string s){throw new Exception(s);} public void LogInfo(string s){} }
}
namespace SailwindCoop.Net {
 public enum Role { Host,Client } public enum LinkState { Connected,Disconnected }
 [Flags] public enum ModFeatures { None=0,EconomyOverhaul=1,SharedMarkets=2 }
 public class CoopNet {
 public class ClockStub { public long LocalTick=1000; public double OffsetMs; } public ClockStub Clock=new ClockStub();
 public Role Role; public LinkState State; public List<object> Sent=new List<object>();
 public ModFeatures HostModFeatures=ModFeatures.EconomyOverhaul|ModFeatures.SharedMarkets;
 public ModFeatures PeerFeatures=ModFeatures.EconomyOverhaul|ModFeatures.SharedMarkets;
 public uint[] ReadyClientIds(ModFeatures required)=>(PeerFeatures&required)==required ? new uint[]{1} : new uint[0];
 public uint PlayerNetIdForPeer(NetPeer p)=>1;
 public ModFeatures FeaturesOf(NetPeer p)=>PeerFeatures;
 public void SendToPlayer(uint id,object m,DeliveryMethod d){Sent.Add(m);} public void Broadcast(object m,DeliveryMethod d){Sent.Add(m);}
 }
 public struct MarketPort { public short Port; public float Bonus; public float[] Supply; }
 public struct MarketEvent {}
 public struct MarketChange { public short Port,Good; public float Delta; }
 public struct MissionEntry {}
 public class EconomyStateMsg { public uint AckSeq; public MarketPort[] Ports=new MarketPort[0]; public bool HasFx,HasDemand; public int FxDay; public double[] Rates=new double[0],Basis=new double[0],Smooth=new double[0],Closes=new double[0]; public MarketEvent[] Events; }
 public class EconomyDeltaMsg { public uint Seq; public MarketChange[] Changes=new MarketChange[0]; public double[] Cargo=new double[0],Shops=new double[0],Rates=new double[0]; }
 public class FinancedCargoMsg { public bool Enabled; public int[] InstanceIds=new int[0]; }
 public class BoatStateMsg { public ushort BoatIndex; public uint NetId; public long Tick; public object RealPos,Rot,RealVel; }
}
namespace SailwindCoop.Sync {
 public static class BoatLocator { public static Transform Boat; public static bool IndicesAuthoritative=true;
 public static Transform FindByIndex(ushort id,out bool unowned){unowned=Boat?.Purchase?.Save?.extraSetting==false;return Boat;} public static int SceneIndexOf(ushort id)=>id; }
 public sealed partial class BoatSync {
 private CoopNet _net;private HashSet<ushort> _warnedUnknown=new HashSet<ushort>();
 private Dictionary<ushort,ClientBoat> _clientBoats=new Dictionary<ushort,ClientBoat>();
 public BoatSync(CoopNet net){_net=net;} private bool InWorld()=>true;public int Tracked=>_clientBoats.Count;
 public class PoseSpy { public void Observe(long a,long b,long c){} public void Push(long a,object b,object c,object d){} }
 public class ClientBoat { public Transform Boat;public PoseSpy Playback=new PoseSpy(),Net=new PoseSpy(); }
 private ClientBoat EnsureClientBoat(ushort id,uint netId,Transform boat){ClientBoat c;if(!_clientBoats.TryGetValue(id,out c))_clientBoats[id]=c=new ClientBoat{Boat=boat};return c;}
 private void RestoreClientBoat(ClientBoat c){}
 }
 public class ItemSync { public static ItemSync Instance=new ItemSync();public int Forwarded;public bool ForwardUnseal(ShipItemCrate item){Forwarded++;return true;} }
 public static partial class ItemPatches { private static void WarnPatch(string s){throw new Exception(s);}public static bool Forward(ShipItemCrate item,bool run)=>PreUnseal(item,run); }
 internal static partial class EconomyOverhaulCompat {
 private static bool IsClient=>true;
 private static string HostOnlyText="host", FinancedText="finance";
 private static void Notify(string s){} private static void Fault(string s,Exception e){throw new Exception(s,e);}
 private const int TradeLoan=1,TradeSell=2; private static bool _inTrade; private static HashSet<int> _tradeIds;
 private static MethodInfo _tradeQuote; private static FieldInfo _tradeUnits,_tradeCargo;
 public sealed class FakeUnit { public Good Cargo; }
 public sealed class FakeBatch { public List<FakeUnit> Units=new List<FakeUnit>(); }
 private static FakeBatch _batch;
 public static FakeBatch QuoteForTest(object ui,object action,object count)=>_batch;
 public static bool BookSale(ShipItem item){
 _tradeQuote=typeof(EconomyOverhaulCompat).GetMethod("QuoteForTest");
 _tradeUnits=typeof(FakeBatch).GetField("Units");_tradeCargo=typeof(FakeUnit).GetField("Cargo");
 _batch=new FakeBatch();_batch.Units.Add(new FakeUnit{Cargo=new Good{Item=item}});
 return TradeBegin(new object[]{null,2,1});
 }
 public static bool Unseal(ShipItemCrate item)=>RefuseFinancedUnseal(item);
 public static bool Sacrifice(Collider other)=>RefuseFinancedSacrifice(other);
 public static bool Use(ShipItem item){bool result=true;return RefuseFinancedUse(item,ref result);}
 public static ShipItem Sale(ShipItem item){RefuseFinancedSale(ref item);return item;}
 public static bool MarketsOn=true, Present=true;
 public static int[] Loans=new int[0]; public static double[] Fx={1,2,3,4};
 public static bool LocalFinanced(ShipItem item)=>item.Save!=null && item.Save.instanceId==7;
 public sealed class LoanRecord { public bool loan; }
 public static Dictionary<int,LoanRecord> TestCargo=new Dictionary<int,LoanRecord>();
 public static int MissionClears;
 private static void MirrorRecords(MissionEntry[] entries){if(entries.Length==0)MissionClears++;}
 private static FieldInfo _creditCargo,_creditLoan;
 private static FieldInfo Field(string type,string name)=>name=="Cargo"?typeof(EconomyOverhaulCompat).GetField("TestCargo"):typeof(LoanRecord).GetField("loan");
 public static int LoanScans, FxScans, PriceRefreshes;
 public static bool ReadFinanced(HashSet<int> ids){LoanScans++;ids.Clear();ids.UnionWith(Loans);return true;}
 public static double[] ReadFx(out int day,out double[] rates,out double[] basis,out double[] smooth,out double[] closes){FxScans++;day=5; rates=(double[])Fx.Clone();basis=smooth=(double[])rates.Clone();closes=new double[0];return rates;}
 public static MarketEvent[] ReadDemand(out double[] view){view=null;return new MarketEvent[0];}
 public static float MarketBonus(int p)=>0;
 public static bool RefreshMarket(IslandMarket m){PriceRefreshes++;int port=Array.FindIndex(Port.ports,p=>p.Market==m);if(m.knownPrices==null)m.knownPrices=new PriceReport[Port.ports.Length]; if(m.knownPrices[port]==null)m.knownPrices[port]=new PriceReport();m.knownPrices[port].day=GameState.day;m.knownPrices[port].approved=true;return true;}
 public static void CheckDemand(int p,int g){} public static void RefreshTradeBook(){} public static void AddFx(double[] a,double[] b,double[] c){}
 public static bool SetMarketBonus(int p,float b)=>false; public static void WriteFx(int d,double[] a,double[] b,double[] c,double[] e){Fx=a;} public static void WriteDemand(MarketEvent[] e){}
 public static void ReadFxQueues(out double[] a,out double[] b,out double[] c){a=b=new double[0];c=(double[])Fx.Clone();}
 }
}
public static class CompatChecks {
 static int passed;
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
 static MarketSync Client(out CoopNet net){
 Time.unscaledTime=0; Port.ports=new[]{new Port{Market=new IslandMarket{currentSupply=new float[]{10}}},new Port{Market=new IslandMarket{currentSupply=new float[]{20}}}};
 net=new CoopNet{Role=Role.Client}; var sync=new MarketSync(net);
 sync.OnEconomyState(new EconomyStateMsg{Ports=new[]{new MarketPort{Port=0,Supply=new float[]{10}},new MarketPort{Port=1,Supply=new float[]{20}}},HasFx=true,Rates=new double[]{1,2,3,4},Basis=new double[]{1,2,3,4},Smooth=new double[]{1,2,3,4}});
 return sync;
 }
 static void Trade(MarketSync sync,int port,float delta){sync.BeginTrade();Port.ports[port].Market.currentSupply[0]+=delta;sync.EndTrade();}
 public static void Run(){
 CoopNet net;var sync=Client(out net);
 Trade(sync,0,-1);sync.OnEconomyState(new EconomyStateMsg{AckSeq=1});
 Check(Port.ports[0].Market.currentSupply[0]==10,"ack-only stock restores host base after compensating trades");
 sync=Client(out net);Trade(sync,0,-1);Trade(sync,0,-2);
 sync.OnEconomyState(new EconomyStateMsg{AckSeq=1});
 Check(Port.ports[0].Market.currentSupply[0]==8,"partial acknowledgement preserves only remaining trade");
 sync.OnEconomyState(new EconomyStateMsg{AckSeq=2});
 Check(Port.ports[0].Market.currentSupply[0]==10,"second ack-only reply removes the final overlay");
 sync=Client(out net);Trade(sync,0,-1);Trade(sync,1,-3);
 sync.OnEconomyState(new EconomyStateMsg{AckSeq=1,Ports=new[]{new MarketPort{Port=1,Supply=new float[]{25}}}});
 Check(Port.ports[0].Market.currentSupply[0]==10 && Port.ports[1].Market.currentSupply[0]==22,"acknowledged port and unrelated pending port reconcile independently");
 sync=Client(out net);sync.BeginTrade();EconomyOverhaulCompat.Fx[0]+=.25;sync.EndTrade();
 sync.OnEconomyState(new EconomyStateMsg{AckSeq=1});
 Check(EconomyOverhaulCompat.Fx[0]==1,"ack-only exchange restores base rates after compensating exchange");
 sync=Client(out net);sync.BeginTrade();EconomyOverhaulCompat.Fx[0]+=.25;sync.EndTrade();
 sync.BeginTrade();EconomyOverhaulCompat.Fx[0]+=.5;sync.EndTrade();sync.OnEconomyState(new EconomyStateMsg{AckSeq=1});
 Check(EconomyOverhaulCompat.Fx[0]==1.5,"partial exchange acknowledgement preserves the remaining rate overlay");
 sync.Clear();sync.OnEconomyState(new EconomyStateMsg{AckSeq=2});
 Check(EconomyOverhaulCompat.Fx[0]==1.5,"session clear discards old host currency base");
 sync=Client(out net);Trade(sync,0,-1);
 var hostNet=new CoopNet{Role=Role.Host};var host=new MarketSync(hostNet);Port.ports[0].Market.currentSupply[0]=10;host.Tick();hostNet.Sent.Clear();
 Port.ports[0].Market.currentSupply[0]++;host.OnEconomyDelta((EconomyDeltaMsg)net.Sent[0],new NetPeer());Time.unscaledTime=1;host.Tick();
 var reply=(EconomyStateMsg)hostNet.Sent[0];sync.OnEconomyState(reply);
 Check(reply.AckSeq==1 && reply.Ports.Length==0 && Port.ports[0].Market.currentSupply[0]==10,"actual host ack-only reply reconciles on actual client");
 net=new CoopNet{Role=Role.Client};var finance=new FinancedCargoSync(net);var item=new ShipItem{Save=new SaveablePrefab{instanceId=7}};
 Check(finance.Blocks(item),"before baseline the loaded loan record protects cargo");
 EconomyOverhaulCompat.TestCargo[7]=new EconomyOverhaulCompat.LoanRecord{loan=true};
 EconomyOverhaulCompat.TestCargo[99]=new EconomyOverhaulCompat.LoanRecord{loan=true};
 finance.Receive(new FinancedCargoMsg{Enabled=true,InstanceIds=new[]{99}});
 Check(!EconomyOverhaulCompat.TestCargo[7].loan && EconomyOverhaulCompat.TestCargo[99].loan,"host snapshot clears settled local EO marker but preserves current debt");
 Check(!finance.Blocks(item),"host removal overrides stale local loan record");item.Save.instanceId=99;
 Check(finance.Blocks(item),"new host loan blocks use without any local EO record");
 var altarContact=new Collider{Item=item};
 Check(!EconomyOverhaulCompat.Sacrifice(altarContact),"altar trigger refuses host-financed cargo without a local credit record");
 Check(EconomyOverhaulCompat.Sacrifice(new Collider()) && EconomyOverhaulCompat.Sacrifice(null),"finance guard leaves non-item altar contacts alone");
 Check(!EconomyOverhaulCompat.Unseal(new ShipItemCrate{Save=item.Save}),"production unseal guard refuses new host loan before relay");
 var crate=new ShipItemCrate{Save=item.Save};ItemPatches.Forward(crate,EconomyOverhaulCompat.Unseal(crate));
 Check(ItemSync.Instance.Forwarded==0,"production PreUnseal does not relay when finance guard skipped original");
 Check(!EconomyOverhaulCompat.Use(item),"production Loans.Use guard reads host loan ids");
 Check(EconomyOverhaulCompat.Sale(item)==null,"production shop guard nulls item before EO prefix can sell it");
 Check(!EconomyOverhaulCompat.BookSale(item),"production book guard refuses financed Quote units before settlement");
 finance.Receive(new FinancedCargoMsg{Enabled=true});Check(!finance.Blocks(item),"host repayment releases cargo");
 Check(EconomyOverhaulCompat.Sacrifice(altarContact),"altar permits cargo once the host settles its loan");
 Check(!EconomyOverhaulCompat.TestCargo[99].loan,"repaid cargo no longer has a local EO loan to deduct again on sale");
 Check(EconomyOverhaulCompat.Unseal(new ShipItemCrate{Save=item.Save}) && EconomyOverhaulCompat.Use(item) && EconomyOverhaulCompat.Sale(item)==item,"production guards permit ordinary repaid cargo");
 ItemPatches.Forward(crate,EconomyOverhaulCompat.Unseal(crate));Check(ItemSync.Instance.Forwarded==1,"ordinary repaid crate still relays once");
 net.HostModFeatures=ModFeatures.None;Check(!finance.Blocks(item),"host without EO leaves ordinary cargo usable");
 EconomyOverhaulCompat.TestCargo[99].loan=true;
 EconomyOverhaulCompat.OnGuestWorldLoaded(net);
 Check(!EconomyOverhaulCompat.TestCargo[99].loan && EconomyOverhaulCompat.MissionClears==1 && net.Sent.Count==0,
       "mixed guest load clears stale loan and mission metadata without a packet");
 net.HostModFeatures=ModFeatures.EconomyOverhaul;
 EconomyOverhaulCompat.TestCargo[99].loan=true;
 EconomyOverhaulCompat.OnGuestWorldLoaded(net);
 Check(EconomyOverhaulCompat.TestCargo[99].loan && EconomyOverhaulCompat.MissionClears==1,
       "EO host leaves cleanup to the authoritative baseline");
 finance.Receive(new FinancedCargoMsg{Enabled=true,InstanceIds=new[]{99}});net.State=LinkState.Disconnected;
 Check(!finance.Blocks(item),"disconnect restores local play");net.State=LinkState.Connected;finance.Clear();
 Check(!finance.Blocks(item),"new session does not retain old host loan ids");
 hostNet=new CoopNet{Role=Role.Host};finance=new FinancedCargoSync(hostNet);EconomyOverhaulCompat.Loans=new[]{99};finance.Tick(true);
 Check(EconomyOverhaulCompat.Sacrifice(altarContact),"host altar retains its authoritative collection and destruction path");
 Check(hostNet.Sent.Count==1 && ((FinancedCargoMsg)hostNet.Sent[0]).InstanceIds.SequenceEqual(new[]{99}),"host publishes newly financed cargo immediately");
 finance.Tick(true);Check(hostNet.Sent.Count==1,"unchanged loans do not create duplicate broadcasts");
 EconomyOverhaulCompat.Loans=new int[0];finance.Tick(true);Check(hostNet.Sent.Count==2,"closing the last loan publishes an empty snapshot");
 var peer=new NetPeer();EconomyOverhaulCompat.Loans=new[]{42};finance.SendBaseline(peer);
 Check(((FinancedCargoMsg)peer.Sent.Single()).InstanceIds.SequenceEqual(new[]{42}),"joining guest receives fresh loan ids independently of scan timer");
 var quietNet=new CoopNet{Role=Role.Host,PeerFeatures=ModFeatures.None};var quietFinance=new FinancedCargoSync(quietNet);
 var quietMarket=new MarketSync(quietNet);var quietPeer=new NetPeer();
 int loanScans=EconomyOverhaulCompat.LoanScans,fxScans=EconomyOverhaulCompat.FxScans;
 quietFinance.Tick(true);quietFinance.SendBaseline(quietPeer);quietMarket.Tick();
 Check(quietNet.Sent.Count==0 && quietPeer.Sent.Count==0 && EconomyOverhaulCompat.LoanScans==loanScans && EconomyOverhaulCompat.FxScans==fxScans,
       "host skips market and finance work when no guest supports it");
 EconomyOverhaulCompat.Present=false;EconomyOverhaulCompat.MarketsOn=false;
 quietNet.PeerFeatures=ModFeatures.EconomyOverhaul|ModFeatures.SharedMarkets;Time.unscaledTime+=2;
 quietFinance.Tick(true);quietFinance.SendBaseline(quietPeer);quietMarket.Tick();
 Check(quietNet.Sent.Count==0 && quietPeer.Sent.Count==0 && EconomyOverhaulCompat.LoanScans==loanScans && EconomyOverhaulCompat.FxScans==fxScans,
       "host without EO skips market and finance work even with an EO guest");
 EconomyOverhaulCompat.Present=true;EconomyOverhaulCompat.MarketsOn=true;
 net=new CoopNet{Role=Role.Client};sync=new MarketSync(net);
 var missing=new Port();
 Port.ports=Enumerable.Range(0,33).Select(i=>i==2 ? missing : new Port{Market=new IslandMarket{currentSupply=new[]{0f}}}).ToArray();
 var full=new EconomyStateMsg{Ports=Enumerable.Range(0,33).Select(i=>new MarketPort{Port=(short)i,Supply=new[]{10f*(i+1)}}).ToArray()};
 Port.Lookups=0;EconomyOverhaulCompat.PriceRefreshes=0;sync.OnEconomyState(full);
 Check(Port.Lookups==33 && EconomyOverhaulCompat.PriceRefreshes==32,"first full snapshot resolves each port once and refreshes its existing markets");
 Time.unscaledTime+=20;Port.Lookups=0;sync.OnEconomyState(full);
 Check(Port.Lookups==1 && EconomyOverhaulCompat.PriceRefreshes==32,"identical full snapshot retries only the missing market and skips price refreshes");
 missing.Market=new IslandMarket{currentSupply=new[]{0f}};Time.unscaledTime+=.1f;
 sync.OnEconomyState(new EconomyStateMsg{Ports=new[]{new MarketPort{Port=2,Supply=new[]{31f}}}});
 sync.Tick();
 Check(missing.Market.currentSupply[0]==0,"late market respects the component lookup timer");
 Trade(sync,2,-2);
 var lateTrade=(EconomyDeltaMsg)net.Sent.Single();
 Check(lateTrade.Changes.Length==1 && lateTrade.Changes[0].Port==2 && lateTrade.Changes[0].Delta==-2,
       "trade can record stock in a newly available market before its retry");
 Time.unscaledTime++;sync.Tick();
 Check(missing.Market.currentSupply[0]==29 && EconomyOverhaulCompat.PriceRefreshes==33,
       "late market applies the latest stored stock with local trades before another packet");
 sync.OnEconomyState(new EconomyStateMsg{AckSeq=lateTrade.Seq,Ports=new[]{new MarketPort{Port=2,Supply=new[]{29f}}}});
 full.Ports[2].Supply[0]=29;
 Port.ports[0].Market.goodsSoftCapOverride=72;sync.OnEconomyState(full);
 Check(EconomyOverhaulCompat.PriceRefreshes==34,"capacity change invalidates matching market stock");
 GameState.day++;sync.OnEconomyState(full);
 Check(EconomyOverhaulCompat.PriceRefreshes==67,"new day refreshes reports even with unchanged stock");
 Port.ports[1].Market.currentSupply[0]=25;full.Ports[1].Supply[0]=25;sync.OnEconomyState(full);
 Check(EconomyOverhaulCompat.PriceRefreshes==68,"stock already applied locally invalidates an older price report");
 net.HostModFeatures=ModFeatures.None;full.Ports[0].Supply[0]=100;sync.OnEconomyState(full);
 Check(Port.ports[0].Market.currentSupply[0]==10,"guest ignores market state without a negotiated host feature");
 var boatNet=new CoopNet{Role=Role.Client};var boats=new BoatSync(boatNet);
 var save=new SaveableObject{extraSetting=true};var root=new Transform{Purchase=new PurchasableBoat{Save=save}};BoatLocator.Boat=root;
 var pose=new BoatStateMsg{BoatIndex=10,NetId=12,Tick=1};boats.OnBoatState(pose,null);
 Check(boats.Tracked==1,"owned hull enters the client pose stream");
 save.extraSetting=false;boats.ReleaseBoat(root);boats.OnBoatState(pose,null);pose.Tick=2;boats.OnBoatState(pose,null);
 Check(boats.Tracked==0,"two late production BoatState callbacks cannot recapture released hull");
 save.extraSetting=true;pose.Tick=3;boats.OnBoatState(pose,null);
 Check(boats.Tracked==1,"repurchased hull accepts production BoatState again");
 boats.ReleaseBoat(root);root.Purchase=null;boats.OnBoatState(pose,null);
 Check(boats.Tracked==1,"non-purchasable mod hull remains eligible for host poses");
 Console.WriteLine("Compatibility logic: "+passed+" passed (stub game/transport; Unity acceptance remains open)");
 }
}
'@
$sources = @('Sync/MarketSync.cs', 'Sync/FinancedCargoSync.cs') | ForEach-Object {
    [regex]::Replace((Get-Content (Join-Path $repo $_) -Raw), '(?m)^using [^\r\n]+;\r?\n', '')
}
# Execute the actual finance entry guards, without loading native Unity objects.
$compat = Get-Content (Join-Path $repo 'Sync/EconomyOverhaulCompat.cs') -Raw
function Get-ProductionMethod([string]$source, [string]$signature) {
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing production guard: $signature" }
    $brace = $source.IndexOf('{', $start)
    $depth = 1; $end = $brace + 1
    while ($depth -gt 0 -and $end -lt $source.Length) {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    $source.Substring($start, $end - $start)
}
$guards = foreach ($signature in @('private static bool RefuseFinancedUnseal(', 'private static bool RefuseFinancedSacrifice(', 'private static void RefuseFinancedSale(', 'private static bool RefuseFinancedUse(', 'private static bool TradeBegin(', 'internal static void OnGuestWorldLoaded(', 'internal static void ClearSettledLoans(')) {
    Get-ProductionMethod $compat $signature
}
$guardSource = 'namespace SailwindCoop.Sync { internal static partial class EconomyOverhaulCompat {' + ($guards -join "`n") + '}}'
$boat = Get-Content (Join-Path $repo 'Sync/BoatSync.cs') -Raw
$boatSource = 'namespace SailwindCoop.Sync { public sealed partial class BoatSync {' + (Get-ProductionMethod $boat 'public void OnBoatState(') + (Get-ProductionMethod $boat 'public void ReleaseBoat(') + '}}'
$items = Get-Content (Join-Path $repo 'Sync/ItemPatches.cs') -Raw
$itemSource = 'namespace SailwindCoop.Sync { public static partial class ItemPatches {' + (Get-ProductionMethod $items 'private static bool PreUnseal(') + '}}'
Add-Type -TypeDefinition ($stub + ($sources -join "`n") + $guardSource + $boatSource + $itemSource) -IgnoreWarnings -WarningAction SilentlyContinue
[CompatChecks]::Run()
