using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Cvars.Validators;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using Gangs.BaseImpl.Extensions;
using Gangs.BaseImpl.Stats;
using Gangs.LastRequestColorPerk;
using GangsAPI;
using GangsAPI.Data;
using GangsAPI.Services;
using GangsAPI.Services.Gang;
using GangsAPI.Services.Player;
using Jailbreak.Formatting.Extensions;
using Jailbreak.Formatting.Views.LastRequest;
using Jailbreak.Public;
using Jailbreak.Public.Extensions;
using Jailbreak.Public.Mod.Damage;
using Jailbreak.Public.Mod.LastGuard;
using Jailbreak.Public.Mod.LastRequest;
using Jailbreak.Public.Mod.LastRequest.Enums;
using Jailbreak.Public.Mod.Rainbow;
using Jailbreak.Public.Mod.Rebel;
using Jailbreak.Public.Mod.Weapon;
using Jailbreak.Public.Utils;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MStatsShared;

namespace Jailbreak.LastRequest;

public class LastRequestManager(ILRLocale messages, IServiceProvider provider)
  : ILastRequestManager, IDamageBlocker {
  
  // ÄNDERUNG: Timer auf 0 gesetzt, damit er nicht mehr auf 60 springt
  public static readonly FakeConVar<int> CV_LR_BASE_TIME =
    new("css_jb_lr_time_base", "Round time to set when LR is activated, 0 to disable", 0);

  public static readonly FakeConVar<int> CV_LR_BONUS_TIME =
    new("css_jb_lr_time_per_lr", "Additional round time to add per LR completion", 0);

  public static readonly FakeConVar<int> CV_LR_GUARD_TIME =
    new("css_jb_lr_time_per_guard", "Additional round time to add per guard", 0);

  public static readonly FakeConVar<int> CV_PRISONER_TO_LR =
    new("css_jb_lr_activate_lr_at", "Number of prisoners to activate LR at", 2,
      ConVarFlags.FCVAR_NONE, new RangeValidator<int>(1, 32));

  // ... (Rest bleibt gleich bis EnableLR)

  public void EnableLR(CCSPlayerController? died = null) {
    messages.LastRequestEnabled().ToAllChat();
    IsLREnabled = true;

    // ÄNDERUNG: Timer-Logik entfernt, damit die Zeit NICHT überschrieben wird.
    // Die Zeit bleibt nun so, wie sie in der Server-Config eingestellt ist.

    var players   = Utilities.GetPlayers();
    var lastGuard = provider.GetService<ILastGuardService>();
    var rebel     = provider.GetService<IRebelService>();
    
    foreach (var player in players) {
      if (!player.PawnIsAlive) continue;

      // ÄNDERUNG: NoBlock aktivieren (Kollision ausschalten)
      player.PlayerPawn.Value!.Collision.CollisionGroup = 11; // CollisionGroup.Debris
      player.PlayerPawn.Value.Collision.CollisionAttribute.CollisionGroup = 11;

      if (player.Team == CsTeam.Terrorist) {
          // ÄNDERUNG: Alle Waffen weg und nur Messer geben
          player.RemoveWeapons();
          player.GiveNamedItem("weapon_knife");
          
          if (died != null && player.SteamID == died.SteamID) continue;
          if (lastGuard is { IsLastGuardActive: true }) rebel?.UnmarkRebel(player);
          player.ExecuteClientCommandFromServer("css_lr");
      }
    }
    // ... (Rest der Funktion bleibt gleich)
  }

  public bool InitiateLastRequest(CCSPlayerController prisoner,
    CCSPlayerController guard, LRType type) {
    
    // ÄNDERUNG: Vor dem Start des Duells nochmal sichergehen: Nur Messer!
    prisoner.RemoveWeapons();
    prisoner.GiveNamedItem("weapon_knife");
    guard.RemoveWeapons();
    guard.GiveNamedItem("weapon_knife");

    var lr = factory!.CreateLastRequest(prisoner, guard, type);
    // ... restliche Logik
    return true;
  }
}
