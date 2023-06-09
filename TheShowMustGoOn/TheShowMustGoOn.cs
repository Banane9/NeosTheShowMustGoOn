using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using BaseX;
using CodeX;
using FrooxEngine;
using FrooxEngine.LogiX;
using FrooxEngine.LogiX.Data;
using FrooxEngine.LogiX.ProgramFlow;
using FrooxEngine.UIX;
using HarmonyLib;
using NeosModLoader;

namespace TheShowMustGoOn
{
    public class TheShowMustGoOn : NeosMod
    {
        public static ModConfiguration Config;

        private static readonly ConditionalWeakTable<Component, object> AudioStreams = new();

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> EnableStreamingWhileUnfocused = new("EnableStreamingWhileUnfocused", "Keep streaming audio to unfocused worlds.", () => true);

        [AutoRegisterConfigKey]
        private static readonly ModConfigurationKey<bool> EnableVoiceWhileUnfocused = new("EnableVoiceWhileUnfocused", "Keep streaming voice to unfocused worlds.", () => false);

        public override string Author => "Banane9";
        public override string Link => "https://github.com/Banane9/NeosTheShowMustGoOn";
        public override string Name => "TheShowMustGoOn";
        public override string Version => "1.0.0";

        public override void OnEngineInit()
        {
            Harmony harmony = new($"{Author}.{Name}");
            Config = GetConfiguration();
            Config.Save(true);
            harmony.PatchAll();
        }

        [HarmonyPatch(typeof(AudioStreamController))]
        private static class AudioStreamControllerPatch
        {
            [HarmonyTranspiler]
            [HarmonyPatch(nameof(AudioStreamController.BuildUI))]
            private static IEnumerable<CodeInstruction> BuildUITranspiler(IEnumerable<CodeInstruction> codeInstructions)
            {
                var attachComponentMethod = typeof(ContainerWorker<Component>).GetMethods(AccessTools.all)
                    .Single(method => method.IsGenericMethodDefinition && method.Name == nameof(ContainerWorker<Component>.AttachComponent))
                    .MakeGenericMethod(typeof(UserAudioStream<StereoSample>));

                foreach (var instruction in codeInstructions)
                {
                    if (instruction.Calls(attachComponentMethod))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = typeof(AudioStreamControllerPatch).GetMethod(nameof(MakeAudioStream), AccessTools.all);
                    }

                    yield return instruction;
                }
            }

            private static UserAudioStream<StereoSample> MakeAudioStream(Slot slot, bool runOnAttachBehavior, Action<UserAudioStream<StereoSample>> beforeAttach)
            {
                var audioStream = slot.AttachComponent(runOnAttachBehavior, beforeAttach);
                AudioStreams.Add(audioStream, null);

                return audioStream;
            }
        }

        [HarmonyPatch]
        private static class UserAudioStreamPatch
        {
            private static bool MuteCheck(Component audioStream)
            {
                var world = audioStream.World;

                return world.Focus == World.WorldFocus.Focused
                    || ((Config.GetValue(EnableVoiceWhileUnfocused)
                            || (Config.GetValue(EnableStreamingWhileUnfocused) && AudioStreams.TryGetValue(audioStream, out _)))
                        && world.Focus == World.WorldFocus.Background);
            }

            private static IEnumerable<MethodBase> TargetMethods()
            {
                var genericOptions = new[]
                {
                    typeof(MonoSample),
                    typeof(StereoSample),
                    typeof(QuadSample),
                    typeof(Surround51Sample)
                };

                return genericOptions
                    .Select(type => typeof(UserAudioStream<>)
                        .MakeGenericType(type)
                        .GetMethod(nameof(UserAudioStream<MonoSample>.OnNewAudioData), AccessTools.all));
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codeInstructions)
            {
                var getWorldFocusMethod = typeof(World).GetProperty(nameof(World.Focus), AccessTools.all).GetMethod;
                var checkMethod = typeof(UserAudioStreamPatch).GetMethod(nameof(MuteCheck), AccessTools.all);

                var instructions = codeInstructions.ToList();
                var getWorldFocusIndex = instructions.FindIndex(instruction => instruction.Calls(getWorldFocusMethod));

                instructions[getWorldFocusIndex - 1] = new CodeInstruction(OpCodes.Ldarg_0);
                instructions[getWorldFocusIndex] = new CodeInstruction(OpCodes.Call, checkMethod);

                instructions.RemoveAt(getWorldFocusIndex + 1);
                instructions[getWorldFocusIndex + 1].opcode = OpCodes.Brfalse_S;

                return instructions;
            }
        }
    }
}