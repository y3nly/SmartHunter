using SmartHunter.Core.Helpers;
using SmartHunter.Game.Data;
using SmartHunter.Game.Data.ViewModels;
using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SmartHunter.Game.Helpers
{
    public static class MhwHelper
    {
        public static bool TryParseHex(string hexString, out long hexNumber)
        {
            return long.TryParse(hexString, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hexNumber);
        }

        public static ulong AddOffset(ulong address, long offset)
        {
            return (ulong)((long)address + offset);
        }

        // TODO: Wouldn't it be nice if all this were data driven?
        public static class DataOffsets
        {
            public static class Monster
            {
                // Doubly linked list
                public static readonly ulong MonsterStartOfStructOffset = 0x40;
                public static readonly ulong NextMonsterOffset = 0x18;
                public static readonly ulong MonsterHealthComponentOffset = 0x7670;
                public static readonly ulong PreviousMonsterOffset = 0x10;
                public static readonly ulong SizeScale = 0x180;
                public static readonly ulong PartCollection = 0x14528;
                public static readonly ulong RemovablePartCollection = PartCollection + 0x22A0 - 0xF0;//0x1ED0;
                public static readonly ulong StatusEffectCollection = 0x19900;
            }

            public static class MonsterModel
            {
                public static readonly int IdLength = 32; // 64?
                public static readonly ulong IdOffset = 0x179;
            }

            public static class MonsterHealthComponent
            {
                public static readonly ulong MaxHealth = 0x60;
                public static readonly ulong CurrentHealth = 0x64;
            }

            public static class MonsterPartCollection
            {
                public static readonly int MaxItemCount = 16;
                public static readonly ulong FirstPart = 0x50;
            }

            public static class MonsterPart
            {
                public static readonly ulong MaxHealth = 0x0C;
                public static readonly ulong CurrentHealth = 0x10;
                public static readonly ulong TimesBrokenCount = 0x18;
                public static readonly ulong NextPart = 0x1F8;//0x3F0;
            }

            public static class MonsterRemovablePartCollection
            {
                public static readonly int MaxItemCount = 32;
                public static readonly ulong FirstRemovablePart = 0x08;
            }

            public static class MonsterRemovablePart
            {
                public static readonly ulong MaxHealth = 0x0C;
                public static readonly ulong CurrentHealth = 0x10;
                public static readonly ulong Validity1 = 0x14;
                public static readonly ulong TimesBrokenCount = 0x18;
                public static readonly ulong Validity2 = 0x28;
                public static readonly ulong Validity3 = 0x40;
                public static readonly ulong NextRemovablePart = 0x78;
            }

            public static class MonsterStatusEffectCollection
            {
                public static int MaxItemCount = 20;
                public static ulong NextStatusEffectPtr = 0x08;
            }

            public static class MonsterStatusEffect
            {
                public static readonly ulong MaxDuration = 0x19C;
                public static readonly ulong CurrentBuildup = 0x1B8;
                public static readonly ulong MaxBuildup = 0x1C8;
                public static readonly ulong CurrentDuration = 0x1F8;
                public static readonly ulong TimesActivatedCount = 0x200;
            }

            public static class PlayerNameCollection
            {
                public static readonly int PlayerNameLength = 32 + 1; // +1 for null terminator
                public static readonly ulong FirstPlayerName = 0x532ED;//0x526AD;
            }

            public static class PlayerDamageCollection
            {
                public static readonly int MaxPlayerCount = 4;
                public static readonly ulong FirstPlayerPtr = 0x48;
                public static readonly ulong NextPlayerPtr = 0x58;
            }

            public static class PlayerDamage
            {
                public static readonly ulong Damage = 0x48;
            }
        }

        public static void UpdatePlayerWidget(Process process, ulong baseAddress, ulong equipmentAddress, ulong weaponAddress)
        {
            for (int index = 0; index < ConfigHelper.PlayerData.Values.StatusEffects.Length; ++index)
            {
                var statusEffectConfig = ConfigHelper.PlayerData.Values.StatusEffects[index];

                ulong sourceAddress = baseAddress;
                if (statusEffectConfig.Source == Config.StatusEffectConfig.MemorySource.Equipment)
                {
                    sourceAddress = equipmentAddress + 0xEFC; // 0xEFC is a base offset for the mantles
                }
                else if (statusEffectConfig.Source == Config.StatusEffectConfig.MemorySource.Weapon)
                {
                    sourceAddress = weaponAddress;
                }
                
                bool allConditionsPassed = true;
                if (statusEffectConfig.Conditions != null)
                {
                    foreach (var condition in statusEffectConfig.Conditions)
                    {
                        bool isOffsetChainValid = true;
                        List<long> offsets = new List<long>();
                        foreach (var offsetString in condition.Offsets)
                        {
                            if (TryParseHex(offsetString, out var offset))
                            {
                                offsets.Add(offset);
                            }
                            else
                            {
                                isOffsetChainValid = false;
                                break;
                            }
                        }

                        if (!isOffsetChainValid)
                        {
                            allConditionsPassed = false;
                            break;
                        }

                        var conditionAddress = MemoryHelper.ReadMultiLevelPointer(false, process, sourceAddress + (ulong)offsets.First(), offsets.Skip(1).ToArray());

                        bool isPassed = false;
                        if (condition.ByteValue.HasValue)
                        {
                            var conditionValue = MemoryHelper.Read<byte>(process, conditionAddress);
                            isPassed = conditionValue == condition.ByteValue;
                        }
                        else if (condition.IntValue.HasValue)
                        {
                            var conditionValue = MemoryHelper.Read<int>(process, conditionAddress);
                            isPassed = conditionValue == condition.IntValue;
                        }
                        else if (condition.StringRegexValue != null)
                        {
                            var conditionValue = MemoryHelper.ReadString(process, conditionAddress, 64);
                            isPassed = new Regex(condition.StringRegexValue).IsMatch(conditionValue);
                        }

                        if (!isPassed)
                        {
                            allConditionsPassed = false;
                            break;
                        }
                    }
                }

                float? timer = null;
                if (allConditionsPassed && statusEffectConfig.TimerOffset != null)
                {
                    if (TryParseHex(statusEffectConfig.TimerOffset, out var timerOffset))
                    {
                        timer = MemoryHelper.Read<float>(process, (ulong)((long)sourceAddress + timerOffset));
                    }

                    if (timer <= 0)
                    {
                        timer = 0;
                        allConditionsPassed = false;
                    }
                }

                OverlayViewModel.Instance.PlayerWidget.Context.UpdateAndGetPlayerStatusEffect(index, timer, allConditionsPassed);
            }
        }

        public static void UpdateTeamWidget(Process process, ulong playerDamageCollectionAddress, ulong playerNameCollectionAddress)
        {
            List<Player> updatedPlayers = new List<Player>();
            for (int playerIndex = 0; playerIndex < DataOffsets.PlayerDamageCollection.MaxPlayerCount; ++playerIndex)
            {
                var player = UpdateAndGetTeamPlayer(process, playerIndex, playerDamageCollectionAddress, playerNameCollectionAddress);
                if (player != null)
                {
                    updatedPlayers.Add(player);
                }
            }

            if (updatedPlayers.Any())
            {
                OverlayViewModel.Instance.TeamWidget.Context.UpdateFractions();
            }
            else if (OverlayViewModel.Instance.TeamWidget.Context.Players.Any())
            {
                OverlayViewModel.Instance.TeamWidget.Context.Players.Clear();
            }
        }

        private static Player UpdateAndGetTeamPlayer(Process process, int playerIndex, ulong playerDamageCollectionAddress, ulong playerNameCollectionAddress)
        {
            Player player = null;

            var playerNameOffset = (ulong)DataOffsets.PlayerNameCollection.PlayerNameLength * (ulong)playerIndex;
            string name = MemoryHelper.ReadString(process, playerNameCollectionAddress + DataOffsets.PlayerNameCollection.FirstPlayerName + playerNameOffset, (uint)DataOffsets.PlayerNameCollection.PlayerNameLength);
            ulong firstPlayerPtr = playerDamageCollectionAddress + DataOffsets.PlayerDamageCollection.FirstPlayerPtr; // check those lines
            ulong currentPlayerPtr = firstPlayerPtr + ((ulong)playerIndex * DataOffsets.PlayerDamageCollection.NextPlayerPtr);
            ulong currentPlayerAddress = MemoryHelper.Read<ulong>(process, currentPlayerPtr);
            int damage = MemoryHelper.Read<int>(process, currentPlayerAddress + DataOffsets.PlayerDamage.Damage);

            if (!String.IsNullOrEmpty(name) || damage > 0)
            {
                player = OverlayViewModel.Instance.TeamWidget.Context.UpdateAndGetPlayer(playerIndex, name, damage);
            }

            return player;
        }

        public static void UpdateMonsterWidget(Process process, ulong monsterBaseList)
        {
            if (monsterBaseList < 0xffffff)
            {
                OverlayViewModel.Instance.MonsterWidget.Context.Monsters.Clear();
                return;
            }

            List<ulong> monsterAddresses = new List<ulong>();

            ulong firstMonster = MemoryHelper.Read<ulong>(process, monsterBaseList + DataOffsets.Monster.PreviousMonsterOffset);

            if (firstMonster == 0x0)
            {
                firstMonster = monsterBaseList;// + DataOffsets.Monster.MonsterStartOfStructOffset;
            }

            firstMonster += DataOffsets.Monster.MonsterStartOfStructOffset;

            ulong currentMonsterAddress = firstMonster;
            while (currentMonsterAddress != 0)
            {
                monsterAddresses.Insert(0, currentMonsterAddress);
                currentMonsterAddress = MemoryHelper.Read<ulong>(process, currentMonsterAddress + DataOffsets.Monster.NextMonsterOffset);
            }

            List<Monster> updatedMonsters = new List<Monster>();
            foreach (var monsterAddress in monsterAddresses)
            {
                var monster = UpdateAndGetMonster(process, monsterAddress);
                if (monster != null)
                {
                    updatedMonsters.Add(monster);
                }
            }

            // Clean out monsters that aren't in the linked list anymore
            var obsoleteMonsters = OverlayViewModel.Instance.MonsterWidget.Context.Monsters.Except(updatedMonsters);
            foreach (var obsoleteMonster in obsoleteMonsters.Reverse())
            {
                OverlayViewModel.Instance.MonsterWidget.Context.Monsters.Remove(obsoleteMonster);
            }
        }

        private static Monster UpdateAndGetMonster(Process process, ulong monsterAddress)
        {
            Monster monster = null;

            ulong tmp = monsterAddress + DataOffsets.Monster.MonsterStartOfStructOffset + DataOffsets.Monster.MonsterHealthComponentOffset;
            ulong health_component = MemoryHelper.Read<ulong>(process, tmp);
            
            string id = MemoryHelper.ReadString(process, tmp + DataOffsets.MonsterModel.IdOffset, (uint)DataOffsets.MonsterModel.IdLength);
            float maxHealth = MemoryHelper.Read<float>(process, health_component + DataOffsets.MonsterHealthComponent.MaxHealth);

            if (String.IsNullOrEmpty(id))
            {
                return monster;
            }

            id = id.Split('\\').Last();
            if (!Monster.IsIncluded(id))
            {
                return monster;
            }

            if (maxHealth <= 0)
            {
                return monster;
            }

            float currentHealth = MemoryHelper.Read<float>(process, health_component + DataOffsets.MonsterHealthComponent.CurrentHealth);
            float sizeScale = MemoryHelper.Read<float>(process, monsterAddress + DataOffsets.Monster.MonsterStartOfStructOffset + DataOffsets.Monster.SizeScale);

            monster = OverlayViewModel.Instance.MonsterWidget.Context.UpdateAndGetMonster(monsterAddress, id, maxHealth, currentHealth, sizeScale);

            
            if (SmartHunter.Game.Helpers.ConfigHelper.MonsterData.Values.Monsters.ContainsKey(id) && SmartHunter.Game.Helpers.ConfigHelper.MonsterData.Values.Monsters[id].Parts.Count() > 0)
            {
                // TODO: I think here we can check if the current player is the host of the party, as if's not there's no point on updating monster parts (cause only the host of the party will see those parts)
                UpdateMonsterParts(process, monster);
                UpdateMonsterRemovableParts(process, monster);
                UpdateMonsterStatusEffects(process, monster);
            }

            return monster;
        }

        private static void UpdateMonsterParts(Process process, Monster monster)
        {
            var parts = monster.Parts.Where(part => !part.IsRemovable);
            if (parts.Any())
            {
                foreach (var part in parts)
                {
                    UpdateMonsterPart(process, monster, part.Address);
                }
            }
            else
            {
                ulong firstPartAddress = monster.Address + DataOffsets.Monster.PartCollection + DataOffsets.MonsterPartCollection.FirstPart;

                for (int index = 0; index < DataOffsets.MonsterPartCollection.MaxItemCount; ++index)
                {
                    ulong currentPartOffset = DataOffsets.MonsterPart.NextPart * (ulong)index;
                    ulong currentPartAddress = firstPartAddress + currentPartOffset;

                    float maxHealth = MemoryHelper.Read<float>(process, currentPartAddress + DataOffsets.MonsterPart.MaxHealth);

                    if (maxHealth > 0)
                    {
                        UpdateMonsterPart(process, monster, currentPartAddress);
                    }
                }
            }
        }

        private static void UpdateMonsterPart(Process process, Monster monster, ulong partAddress)
        {
            float maxHealth = MemoryHelper.Read<float>(process, partAddress + DataOffsets.MonsterPart.MaxHealth);
            float currentHealth = MemoryHelper.Read<float>(process, partAddress + DataOffsets.MonsterPart.CurrentHealth);
            int timesBrokenCount = MemoryHelper.Read<int>(process, partAddress + DataOffsets.MonsterPart.TimesBrokenCount);

            monster.UpdateAndGetPart(partAddress, false, maxHealth, currentHealth, timesBrokenCount);
        }

        private static void UpdateMonsterRemovableParts(Process process, Monster monster)
        {
            var removableParts = monster.Parts.Where(part => part.IsRemovable);
            if (removableParts.Any())
            {
                foreach (var removablePart in removableParts)
                {
                    UpdateMonsterRemovablePart(process, monster, removablePart.Address);
                }
            }
            else
            {
                ulong removablePartAddress = monster.Address + DataOffsets.Monster.RemovablePartCollection + DataOffsets.MonsterRemovablePartCollection.FirstRemovablePart;
                for (int index = 0; index < DataOffsets.MonsterRemovablePartCollection.MaxItemCount; ++index)
                {
                    // Every 16 elements there seems to be a new removable part collection. When we reach this point,
                    // we advance past the first 64 bit field to get to the start of the next part again
                    ulong staticPtr = MemoryHelper.Read<ulong>(process, removablePartAddress);
                    if (staticPtr <= 10)
                    {
                        removablePartAddress += 8;
                    }
                    
                    // This is rough/hacky but it removes seemingly valid parts that aren't actually "removable".
                    // TODO: Figure out why Paolumu, Barroth, Radobaan have these mysterious removable parts
                    bool isValid1 = true;
                    bool isValid2 = true;
                    bool isValid3 = true;

                    int validity1 = MemoryHelper.Read<int>(process, removablePartAddress + DataOffsets.MonsterRemovablePart.Validity1);
                    isValid1 = validity1 == 1;
                    if (!ConfigHelper.Main.Values.Debug.ShowWeirdRemovableParts)
                    {                                           
                        int validity2 = MemoryHelper.Read<int>(process, removablePartAddress + DataOffsets.MonsterRemovablePart.Validity2);
                        int validity3 = MemoryHelper.Read<int>(process, removablePartAddress + DataOffsets.MonsterRemovablePart.Validity3);

                        isValid2 = validity3 == 0 || validity3 == 1;

                        isValid3 = true;
                        if (validity3 == 0 && validity2 != 1)
                        {
                            isValid3 = false;
                        }
                    }

                    if (isValid1 && isValid2 && isValid3)
                    {
                        float maxHealth = MemoryHelper.Read<float>(process, removablePartAddress + DataOffsets.MonsterRemovablePart.MaxHealth);
                        if (maxHealth > 0)
                        {
                            UpdateMonsterRemovablePart(process, monster, removablePartAddress);
                        }
                        else
                        {
                            break;
                        }
                    }

                    removablePartAddress += DataOffsets.MonsterRemovablePart.NextRemovablePart;
                }
            }
        }

        private static void UpdateMonsterRemovablePart(Process process, Monster monster, ulong removablePartAddress)
        {
            float maxHealth = MemoryHelper.Read<float>(process, removablePartAddress + DataOffsets.MonsterRemovablePart.MaxHealth);
            float currentHealth = MemoryHelper.Read<float>(process, removablePartAddress + DataOffsets.MonsterRemovablePart.CurrentHealth);
            int timesBrokenCount = MemoryHelper.Read<int>(process, removablePartAddress + DataOffsets.MonsterRemovablePart.TimesBrokenCount);

            monster.UpdateAndGetPart(removablePartAddress, true, maxHealth, currentHealth, timesBrokenCount);
        }

        private static void UpdateMonsterStatusEffects(Process process, Monster monster)
        {
            int maxIndex = ConfigHelper.MonsterData.Values.StatusEffects.Where(s => s.GroupId.Equals("StatusEffect")).Count() - 1;
            var statuses = monster.StatusEffects;
            if (statuses.Where(s => s.GroupId.Equals("StatusEffect")).Any())
            {
                foreach (MonsterStatusEffect status in statuses)
                {
                    float currentBuildup = 0;
                    float maxBuildup = MemoryHelper.Read<float>(process, status.Address + DataOffsets.MonsterStatusEffect.MaxBuildup);
                    if (maxBuildup > 0)
                    {
                        currentBuildup = MemoryHelper.Read<float>(process, status.Address + DataOffsets.MonsterStatusEffect.CurrentBuildup);
                    }
                    float currentDuration = 0;
                    float maxDuration = MemoryHelper.Read<float>(process, status.Address + DataOffsets.MonsterStatusEffect.MaxDuration);
                    if (maxDuration > 0)
                    {
                        currentDuration = MemoryHelper.Read<float>(process, status.Address + DataOffsets.MonsterStatusEffect.CurrentDuration);
                    }
                    int timesActivatedCount = MemoryHelper.Read<int>(process, status.Address + DataOffsets.MonsterStatusEffect.TimesActivatedCount);

                    if (maxBuildup > 0 || maxDuration > 0)
                    {
                        uint index = MemoryHelper.Read<uint>(process, status.Address + 0x198);
                        if (index <= maxIndex)
                        {
                            var statusEffectConfig = ConfigHelper.MonsterData.Values.StatusEffects[index];
                            monster.UpdateAndGetStatusEffect(status.Address, (int)index, maxBuildup > 0 ? maxBuildup : 1, !statusEffectConfig.InvertBuildup ? currentBuildup : maxBuildup - currentBuildup, maxDuration, !statusEffectConfig.InvertDuration ? currentDuration : maxDuration - currentDuration, timesActivatedCount);
                        }
                    }
                }
            }
            else
            {
                ulong baseStatus = MemoryHelper.Read<ulong>(process, monster.Address + DataOffsets.Monster.MonsterStartOfStructOffset + 0x78);
                baseStatus = MemoryHelper.Read<ulong>(process, baseStatus + 0x57A8);
                ulong nani = baseStatus;
                while (nani != 0)
                {
                    nani = MemoryHelper.Read<ulong>(process, nani + 0x10);
                    if (nani != 0)
                    {
                        baseStatus = nani;
                    }
                }
                ulong currentStatusPointer = baseStatus + 0x40;
                while (currentStatusPointer != 0x0)
                {
                    var currentMonsterInStatus = MemoryHelper.Read<ulong>(process, currentStatusPointer + 0x188);
                    if (currentMonsterInStatus == monster.Address + 0x40 && !monster.StatusEffects.Where(status => status.Address == currentStatusPointer).Any())
                    {
                        float currentBuildup = 0;
                        float maxBuildup = MemoryHelper.Read<float>(process, currentStatusPointer + DataOffsets.MonsterStatusEffect.MaxBuildup);
                        if (maxBuildup > 0)
                        {
                            currentBuildup = MemoryHelper.Read<float>(process, currentStatusPointer + DataOffsets.MonsterStatusEffect.CurrentBuildup);
                        }
                        float currentDuration = 0;
                        float maxDuration = MemoryHelper.Read<float>(process, currentStatusPointer + DataOffsets.MonsterStatusEffect.MaxDuration);
                        if (maxDuration > 0)
                        {
                            currentDuration = MemoryHelper.Read<float>(process, currentStatusPointer + DataOffsets.MonsterStatusEffect.CurrentDuration);
                        }
                        int timesActivatedCount = MemoryHelper.Read<int>(process, currentStatusPointer + DataOffsets.MonsterStatusEffect.TimesActivatedCount);

                        if (maxBuildup > 0 || maxDuration > 0)
                        {
                            uint index = MemoryHelper.Read<uint>(process, currentStatusPointer + 0x198);
                            if (index <= maxIndex && !((index == 14 || index == 15) && monster.isElder)) // skip traps for elders
                            {
                                var statusEffectConfig = ConfigHelper.MonsterData.Values.StatusEffects[index];
                                monster.UpdateAndGetStatusEffect(currentStatusPointer, (int)index, maxBuildup > 0 ? maxBuildup : 1, !statusEffectConfig.InvertBuildup ? currentBuildup : maxBuildup - currentBuildup, maxDuration, !statusEffectConfig.InvertDuration ? currentDuration : maxDuration - currentDuration, timesActivatedCount);
                            }
                        }
                    }
                    currentStatusPointer = MemoryHelper.Read<ulong>(process, currentStatusPointer + 0x18);
                }
            }

            // Rage
            ulong rageAddress = monster.Address + 0x1BE30; //0x1BE20
            float maxRageBuildUp = MemoryHelper.Read<float>(process, rageAddress + 0x24);
            float currentRageBuildUp = 0;
            if (maxRageBuildUp > 0)
            {
                currentRageBuildUp = MemoryHelper.Read<float>(process, rageAddress);
            }
            float maxRageDuration = MemoryHelper.Read<float>(process, rageAddress + 0x10);
            float currentRageDuration = 0;
            if (maxRageDuration > 0)
            {
                currentRageDuration = MemoryHelper.Read<float>(process, rageAddress + 0xC);
            }
            int rageActivatedCount = MemoryHelper.Read<int>(process, rageAddress + 0x1C);
            var rageStatusEffect = ConfigHelper.MonsterData.Values.StatusEffects.SingleOrDefault(s => s.GroupId.Equals("Rage"));//[33]; // 33 is rage
            if (maxRageBuildUp > 0 || maxRageDuration > 0)
            {
                monster.UpdateAndGetStatusEffect(rageAddress, Array.IndexOf(ConfigHelper.MonsterData.Values.StatusEffects, rageStatusEffect), maxRageBuildUp > 0 ? maxRageBuildUp : 1, !rageStatusEffect.InvertBuildup ? currentRageBuildUp : maxRageBuildUp - currentRageBuildUp, maxRageDuration, !rageStatusEffect.InvertDuration ? currentRageDuration : maxRageDuration - currentRageDuration, rageActivatedCount);
            }
        }
    }
}
