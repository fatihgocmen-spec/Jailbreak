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
  
  public static readonly FakeConVar<int> CV_LR_BASE_TIME = new("css_jb_lr_time_base", "Round time to set when LR is activated", 0);
  public static readonly FakeConVar<int> CV_LR_BONUS_TIME = new("css_jb_lr_time_per_lr", "Additional round time to add", 0);
  public static readonly FakeConVar<int> CV_LR_GUARD_TIME = new("css_jb_lr_time_per_guard", "Additional time per guard", 0);
  public static readonly FakeConVar<int> CV_PRISONER_TO_LR = new("css_jb_lr_activate_lr_at", "Prisoners for LR", 2, ConVarFlags.FCVAR_NONE, new RangeValidator<int>(1, 32));
  public static readonly FakeConVar<int> CV_MIN_PLAYERS_FOR_CREDITS = new("css_jb_min_players_for_credits", "Min players for credits", 5);
  public static readonly FakeConVar<int> CV_MAX_TIME_FOR_LR = new("css_jb_max_time_for_lr", "Max round time during LR", 0);

  private readonly IRainbowColorizer rainbowColorizer = provider.GetRequiredService<IRainbowColorizer>();
  private ILastRequestFactory? factory;
  public bool IsLREnabledForRound { get; set; } = true;
  public bool IsLREnabled { get; set; }
  public IList<AbstractLastRequest> ActiveLRs { get; } = new List<AbstractLastRequest>();

  public bool ShouldBlockDamage(CCSPlayerController victim, CCSPlayerController? attacker) {
    if (!IsLREnabled) return false;
    if (attacker == null || !attacker.IsReal()) return false;

    var victimLR = ((ILastRequestManager)this).GetActiveLR(victim);
    var attackerLR = ((ILastRequestManager)this).GetActiveLR(attacker);

    // Schutz: Wenn einer im LR ist, der andere aber nicht -> Block
    if (victimLR != attackerLR) {
      messages.DamageBlockedInsideLastRequest.ToCenter(attacker);
      return true;
    }

    // Wenn beide im selben LR sind, prüfen ob sie Gegner sind
    if (victimLR != null) {
      if (victimLR.Prisoner.Slot == attacker.Slot || victimLR.Guard.Slot == attacker.Slot) return false;
      messages.DamageBlockedNotInSameLR.ToCenter(attacker);
      return true;
    }
    return false;
  }

  public void Start(BasePlugin basePlugin) {
    factory = provider.GetRequiredService<ILastRequestFactory>();
    if (API.Gangs != null) {
        var stats = API.Gangs.Services.GetService<IStatManager>();
        stats?.Stats.Add(new LRStat());
    }

    // FIX: NoBlock für die gesamte Runde (beim Spawn aktiviert)
    basePlugin.RegisterEventHandler<EventPlayerSpawn>((@event, info) => {
        var player = @event.Userid;
        if (player != null && player.IsValid && player.PlayerPawn.Value != null) {
            player.PlayerPawn.Value.Collision.CollisionGroup = 11;
            player.PlayerPawn.Value.Collision.CollisionAttribute.CollisionGroup = 11;
        }
        return HookResult.Continue;
    });

    basePlugin.RegisterListener<Listeners.OnEntityParentChanged>(OnDrop);
    VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Hook(OnCanAcquire, HookMode.Pre);
  }

  public void Dispose() {
    VirtualFunctions.CCSPlayer_ItemServices_CanAcquireFunc.Unhook(OnCanAcquire, HookMode.Pre);
  }

  public void DisableLR() { IsLREnabled = false; }
  public void DisableLRForRound() { DisableLR(); IsLREnabledForRound = false; }

  public void EnableLR(CCSPlayerController? died = null) {
    messages.LastRequestEnabled().ToAllChat();
    IsLREnabled = true;
    
    foreach (var player in Utilities.GetPlayers()) {
      if (!player.PawnIsAlive) continue;

      if (player.Team == CsTeam.Terrorist) {
          player.RemoveWeapons();
          player.GiveNamedItem("weapon_knife");
          player.ExecuteClientCommandFromServer("css_lr");
      }
    }
  }

  public bool InitiateLastRequest(CCSPlayerController prisoner, CCSPlayerController guard, LRType type) {
    // FIX: Waffen-Reset bei Duell-Start
    prisoner.RemoveWeapons(); prisoner.GiveNamedItem("weapon_knife");
    guard.RemoveWeapons(); guard.GiveNamedItem("weapon_knife");

    var lr = factory!.CreateLastRequest(prisoner, guard, type);
    if (lr is ILastRequestConfig configurable && configurable.RequiresConfiguration) {
      configurable.OpenConfigMenu(prisoner, guard, () => { completeLRInitiation(lr, prisoner, guard); });
    } else {
      completeLRInitiation(lr, prisoner, guard);
    }
    return true;
  }

  private void completeLRInitiation(AbstractLastRequest lr, CCSPlayerController prisoner, CCSPlayerController guard) {
    lr.Setup();
    ActiveLRs.Add(lr);
    prisoner.SetHealth(100); guard.SetHealth(100);
    messages.InformLastRequest(lr).ToAllChat();
  }

  private void OnDrop(CEntityInstance entity, CEntityInstance newparent) {
    if (!entity.IsValid || !IsLREnabled) return;
    if (!Tag.WEAPONS.Contains(entity.DesignerName)) return;
    var weaponEntity = Utilities.GetEntityFromIndex<CCSWeaponBase>((int)entity.Index);
    if (weaponEntity == null || !weaponEntity.IsValid) return;
    if (newparent.IsValid) return;
    weaponEntity.SetColor(Color.White);
  }

  private HookResult OnCanAcquire(DynamicHook hook) {
    if (!IsLREnabled) return HookResult.Continue;
    var player = hook.GetParam<CCSPlayer_ItemServices>(0).Pawn.Value.Controller.Value?.As<CCSPlayerController>();
    if (player == null || !player.IsValid) return HookResult.Continue;
    if (hook.GetParam<AcquireMethod>(2) != AcquireMethod.PickUp) return HookResult.Continue;
    if (((ILastRequestManager)this).GetActiveLR(player) != null) return HookResult.Handled;
    return HookResult.Continue;
  }
}
