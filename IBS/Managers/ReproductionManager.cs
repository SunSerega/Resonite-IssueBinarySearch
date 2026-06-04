using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using IBS.Common;
using IBS.Helpers;

using ResoniteLink;
using ResoniteLink.RPath;

using Spectre.Console;

namespace IBS.Managers;

public static class ReproductionManager
{
    private static readonly Stack<SlotModification> successful_mods = [];

    static ReproductionManager()
    {
        if (!Directory.Exists(Constants.SuccessPath))
            return;
        for (Int32 i = 1; ; i++)
        {
            var path = Path.Combine(Constants.SuccessPath, $"{i}.bin");
            if (!File.Exists(path))
                break;
            var mod = SlotModification.Load(path);
            successful_mods.Push(mod);
        }
    }

    public static Int32 SuccessfulModCount => successful_mods.Count;

    public static async Task RunExploration()
    {
        if (successful_mods.TryPeek(out var org_modification))
        {
            lock (Utils.OutLock)
            {
                AnsiConsole.MarkupLineInterpolated($"[green]Old run last modification:[/]");
                org_modification.WriteToConsole();
            }
        }
        else
        {
            org_modification =  new SlotModification.SoftSkip()
            {
                SlotDescr = "Root",
            };
        }

        if (!await TryReproduce(try_descr: "org reproduction", org_modification))
            throw new InvalidOperationException($"Failed to reproduce issue");

        var final_mod = await ExploreMod(path_descr: "Root", Query.Root, org_modification, m => m);
        if (final_mod == org_modification)
        {
            lock (Utils.OutLock)
                AnsiConsole.MarkupLineInterpolated($"[red]Couldn't find any modification preserving the issue[/]");
        }
        lock (Utils.OutLock)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]Final modification:[/]");
            final_mod.WriteToConsole();
        }

        HeadlessManager.EnsureRunning();
        HeadfullManager.KillIfRunning();

        using (var link = await ResoLinkManager.Connect(req_cancel_token: default))
        {
            lock (Utils.OutLock)
                AnsiConsole.MarkupLineInterpolated($"[aqua]Applying final modifications[/]");
            var root = await Query.Root.Single(link);
            await final_mod.ApplyToRoot(link, root);
        }

        lock (Utils.OutLock)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]MRE found, waiting for you to check it out[/]");
            AnsiConsole.MarkupLineInterpolated($"[green]Press enter to exit[/]");
        }
        Console.ReadLine();
    }

    private static async Task<SlotModification> ExploreMod(String path_descr, Query<ResoniteLink.Slot> q_slot, SlotModification mod, Func<SlotModification, SlotModification> re_assemble_root)
    {
        //TODO This doesn't find MRE if deleting some of the slots allows to delete other slots/components
        // - If that's an issue, I need to store attempt number and treat results of older attempts as something that can be tried again
        return mod switch
        {
            SlotModification.HardSkip or SlotModification.Delete or SlotModification.DeleteWithoutChildren => mod,
            SlotModification.SoftSkip ss_mod => await ExploreModSoftSkip(path_descr, q_slot, ss_mod, re_assemble_root),
            SlotModification.Nested nested_mod => await ExploreModNested(path_descr, q_slot, nested_mod, re_assemble_root),
            _ => throw new NotImplementedException($"Unexpected modification type: {mod.GetType().Name}"),
        };
    }

    private static async Task<SlotModification> ExploreModSoftSkip(String path_descr, Query<ResoniteLink.Slot> q_slot, SlotModification.SoftSkip ss_mod, Func<SlotModification, SlotModification> re_assemble_root)
    {
        SlotModification.Nested new_nested_mod;
        using (var link = await ResoLinkManager.Connect(req_cancel_token: default))
        {
            var slot_as_list = await q_slot.ToList(link);

            //TODO Still sometimes gets 0 results, didn't get to reproduce this reliably tho
            //if (slot_as_list.Count == 0)
            //    return new SlotModification.HardSkip() { SlotDescr = ss_mod.SlotDescr };
            if (slot_as_list.Count != 1)
                throw new InvalidOperationException($"Slot query for {path_descr} somehow returned {slot_as_list.Count} results");

            var slot = slot_as_list.Single();
            if (slot.Children is null)
            {
                lock (Utils.OutLock)
                    AnsiConsole.MarkupLineInterpolated($"[yellow]Slot at {path_descr} has null children, treating as empty[/]");
                slot.Children = [];
            }

            new_nested_mod = new SlotModification.Nested()
            {
                SlotDescr = ss_mod.SlotDescr,
                Children = slot.Children.ToArray(child => new SlotModification.SoftSkip() { SlotDescr = $"{child.Name.Value} ({child.Tag.Value})" }),
                ComponentDelete = new Boolean?[slot.Components.Count],
            };
        }

        var explored_nested_mod = await ExploreModNested(path_descr, q_slot, new_nested_mod, re_assemble_root);
        if (explored_nested_mod == new_nested_mod)
            return new SlotModification.HardSkip() { SlotDescr = ss_mod.SlotDescr };

        return explored_nested_mod;
    }

    private static async Task<SlotModification> ExploreModNested(String path_descr, Query<ResoniteLink.Slot> q_slot, SlotModification.Nested nested_mod, Func<SlotModification, SlotModification> re_assemble_root)
    {
        nested_mod = await ExploreModNestedChildren(path_descr, q_slot, nested_mod, re_assemble_root);
        nested_mod = await ExploreModNestedComponents(path_descr, nested_mod, re_assemble_root);
        return await ExploreModNestedFlatten(path_descr, nested_mod, re_assemble_root);
    }

    private static async Task<SlotModification.Nested> ExploreModNestedChildren(String path_descr, Query<ResoniteLink.Slot> q_slot, SlotModification.Nested nested_mod, Func<SlotModification, SlotModification> re_assemble_root)
    {
        var children = nested_mod.Children.ToArray();
        var component_delete = nested_mod.ComponentDelete;
        var any_changes = false;

        SlotModification.Nested CreateNewNested() => new()
        {
            SlotDescr = nested_mod.SlotDescr,
            Children = children,
            ComponentDelete = component_delete,
        };

        var soft_skip_inds = new List<Int32>(children.Length);
        for (Int32 i = 0; i < children.Length; i++)
        {
            var child_mod = children[i];
            if (child_mod is SlotModification.SoftSkip)
                soft_skip_inds.Add(i);
        }
        var del_inds = await BinarySearchMaxOptions(search_descr: $"soft skip children ({soft_skip_inds.Count}/{children.Length}) in {path_descr}", soft_skip_inds, option =>
        {
            foreach (var i in soft_skip_inds)
                children[i] = option.Contains(i) ? new SlotModification.Delete() { SlotDescr = nested_mod.Children[i].SlotDescr } : nested_mod.Children[i];
            return re_assemble_root(CreateNewNested());
        });
        foreach (var i in soft_skip_inds)
            children[i] = del_inds.Contains(i) ? new SlotModification.Delete() { SlotDescr = nested_mod.Children[i].SlotDescr } : nested_mod.Children[i];
        any_changes |= del_inds.Count != 0;

        for (Int32 i = 0; i < children.Length; i++)
        {
            var child_mod = children[i];
            var new_child_mod = await ExploreMod(path_descr: $"{path_descr}[{i}] => {child_mod.SlotDescr}", q_slot.Children(includeComponents: false).At(i), child_mod, new_child_mod =>
            {
                children[i] = new_child_mod;
                return re_assemble_root(CreateNewNested());
            });
            children[i] = new_child_mod;
            any_changes |= new_child_mod != child_mod;
        }

        if (!any_changes)
            return nested_mod;
        return CreateNewNested();
    }

    private static async Task<SlotModification.Nested> ExploreModNestedComponents(String path_descr, SlotModification.Nested nested_mod, Func<SlotModification, SlotModification> re_assemble_root)
    {
        if (nested_mod.ComponentDelete.All(x => x is { }))
            return nested_mod;
        var children = nested_mod.Children;
        var component_delete = nested_mod.ComponentDelete.ToArray();

        SlotModification.Nested CreateNewNested() => new()
        {
            SlotDescr = nested_mod.SlotDescr,
            Children = children,
            ComponentDelete = component_delete,
        };

        var inds_in_question = new List<Int32>(component_delete.Length);
        for (Int32 i = 0; i < component_delete.Length; i++)
        {
            if (component_delete[i] is null)
                inds_in_question.Add(i);
        }
        var del_inds = await BinarySearchMaxOptions(search_descr: $"questioned components ({inds_in_question.Count}/{component_delete.Length}) in {path_descr}", inds_in_question, option =>
        {
            foreach (var i in inds_in_question)
                component_delete[i] = option.Contains(i) ? true : null;
            return re_assemble_root(CreateNewNested());
        });

        foreach (var i in inds_in_question)
            component_delete[i] = del_inds.Contains(i);
        return CreateNewNested();
    }

    private static async Task<SlotModification> ExploreModNestedFlatten(String path_descr, SlotModification.Nested nested_mod, Func<SlotModification, SlotModification> re_assemble_root)
    {
        if (nested_mod.ComponentDelete.Any(d => d is false))
            return nested_mod;

        if (nested_mod.Children.All(c => c is SlotModification.Delete))
        {
            var delete_mod = new SlotModification.Delete()
            {
                SlotDescr = nested_mod.SlotDescr,
            };
            if (await TryReproduce(try_descr: $"delete instead of flatten {path_descr}", re_assemble_root(delete_mod)))
                return delete_mod;
        }

        var flatten_mod = new SlotModification.DeleteWithoutChildren()
        {
            SlotDescr = nested_mod.SlotDescr,
            Children = nested_mod.Children,
        };
        if (await TryReproduce(try_descr: $"flatten {path_descr}", re_assemble_root(flatten_mod)))
            return flatten_mod;

        return nested_mod;
    }

    private static async Task<List<Int32>> BinarySearchMaxOptions(String search_descr, List<Int32> inds_in_question, Func<List<Int32>, SlotModification> re_assemble_root)
    {
        if (inds_in_question.Count == 0)
            return [];
        if (await TryReproduce(try_descr: $"reproducing without all {search_descr}", re_assemble_root(inds_in_question)))
            return inds_in_question;
        if (inds_in_question.Count == 1)
            return [];

        var inds_confirmed = new List<Int32>(inds_in_question.Count);
        var last_try_count = inds_in_question.Count;
        while (true)
        {
            var try_count = (last_try_count + 1) / 2;
            if (try_count > inds_in_question.Count)
                try_count = inds_in_question.Count;

            var any_new_confirmed = false;
            var try_inds = new List<Int32>(try_count);
            for (var offset = 0; offset < inds_in_question.Count; offset += try_count)
            {
                try_inds.AddRange(inds_confirmed);
                try_inds.AddRange(inds_in_question.Skip(offset).Take(try_count));
                if (await TryReproduce(try_descr: $"reproducing without {try_inds.Count} ({inds_confirmed.Count}+..{try_count}) (inds={String.Join(',', try_inds)}) of {search_descr}", re_assemble_root(try_inds)))
                {
                    any_new_confirmed = true;
                    inds_confirmed.AddRange(try_inds.Skip(inds_confirmed.Count));
                    var try_inds_hs = try_inds.ToHashSet();
                    inds_in_question.RemoveAll(try_inds_hs.Contains);
                }
                try_inds.Clear();
            }

            if (try_count is 1 && !any_new_confirmed)
                break;
            last_try_count = try_count;
        }

        if (inds_confirmed.Count == 0)
        {
            var repro_tries = 0;
            while (true)
            {
                repro_tries += 1;
                if (await TryReproduce(try_descr: $"panic reproducing with all of {search_descr}", re_assemble_root(inds_confirmed)))
                    break;
                lock (Utils.OutLock)
                {
                    AnsiConsole.MarkupLineInterpolated($"[red]Failed to reproduce with no changes. Maybe the issue is not deterministic (try #{repro_tries})...[/]");
                    if (repro_tries % 10 == 0)
                    {
                        if (!AnsiConsole.Confirm("Continue trying?", defaultValue: true))
                            throw new InvalidOperationException($"Canceled by user due to failure to reproduce. Try to delete some of the last states in:\n{Path.GetFullPath(Constants.SuccessPath)}");
                    }
                }
            }
        }

        return inds_confirmed;
    }

    private static async Task<Boolean> TryReproduce(String try_descr, SlotModification root_mod)
    {
        lock (Utils.OutLock)
            AnsiConsole.MarkupLineInterpolated($"[aqua]Starting reproduction: {try_descr}[/]");
        var p_headless = HeadlessManager.EnsureRunning();
        var p_headfull = HeadfullManager.EnsureRunning();

        Int32 root_children_count;
        using (var link = await ResoLinkManager.Connect(req_cancel_token: default))
        {
            lock (Utils.OutLock)
                AnsiConsole.MarkupLineInterpolated($"[aqua]Getting root[/]");
            var root = await Query.Root.Single(link);
            root_children_count = root.Children.Count;
            lock (Utils.OutLock)
                AnsiConsole.MarkupLineInterpolated($"[aqua]Applying modifications[/]");
            try
            {
                await root_mod.ApplyToRoot(link, root);
            }
            catch (Exception ex)
            {
                lock (Utils.OutLock)
                    AnsiConsole.MarkupLineInterpolated($"[red]Failed to apply modification for: {try_descr}\n{ex}[/]");
                await HeadlessManager.EnsureRestarted(root_children_count);
                return false;
            }
        }
        lock (Utils.OutLock)
            AnsiConsole.MarkupLineInterpolated($"[aqua]Waiting and inviting client session[/]");

        WsManager.WaitForClientSession();
        p_headless.StandardInput.WriteLine($"spawn {Constants.SessionInitItemResRec}");

        lock (Utils.OutLock)
            AnsiConsole.MarkupLineInterpolated($"[aqua]Waiting for results[/]");
        var sw = Stopwatch.StartNew();
        var has_exited = p_headfull.WaitForExit(TimeSpan.FromMinutes(2)); //TODO Make timeout configurable?
        lock (Utils.OutLock)
        {
            if (has_exited)
                AnsiConsole.MarkupLineInterpolated($"[aqua]Headfull process exited after {sw.Elapsed} with exit code {p_headfull.ExitCode}[/]");
            else
                AnsiConsole.MarkupLineInterpolated($"[aqua]Headfull process still running after {sw.Elapsed}[/]");
        }

        lock (Utils.OutLock)
            AnsiConsole.MarkupLineInterpolated($"[green]Reproduction attempt: {(has_exited ? "Succeeded" : "Failed")}[/]");
        //TODO Need deep comparison, cannot rely on mod references alone
        if (has_exited && !(successful_mods.TryPeek(out var last_mod) && last_mod == root_mod))
        {
            successful_mods.Push(root_mod);
            Directory.CreateDirectory(Constants.SuccessPath);
            root_mod.Save(Path.Combine(Constants.SuccessPath, $"{successful_mods.Count}.bin"));
            lock (Utils.OutLock)
            {
                AnsiConsole.MarkupLineInterpolated($"[aqua]New successful set of modifications:[/]");
                root_mod.WriteToConsole();
            }
        }

        await HeadlessManager.EnsureRestarted(root_children_count);
        await Task.Delay(TimeSpan.FromSeconds(10)); //TODO Somehow wait for StreamVR to be free?
        HeadfullManager.EnsureRunning();

        return has_exited;
    }

    public abstract class SlotModification
    {
        public required String SlotDescr { get; init; }

        public abstract Task Apply(String slot_path, LinkInterface link, ResoniteLink.Slot target, Reference? override_parent);
        public Task ApplyToRoot(LinkInterface link, ResoniteLink.Slot root) => this.Apply(slot_path: "Root", link, root, override_parent: null);

        private enum Kind : Byte
        {
            SoftSkip = 0,
            HardSkip = 1,
            Delete = 2,
            Nested = 3,
            DeleteWithoutChildren = 4,
        }

        public abstract void Save(BinaryWriter bw);
        public void Save(String file_path)
        {
            using var fs = new FileStream(file_path, FileMode.CreateNew, FileAccess.ReadWrite);
            var bw = new BinaryWriter(fs);
            this.Save(bw);
            fs.Position = 0;
            var br = new BinaryReader(fs);
            _ = Load(br); // Test Immediately
        }

        public static SlotModification Load(BinaryReader br)
        {
            var kind = br.ReadEnum<Kind>();
            return kind switch
            {
                Kind.SoftSkip => SoftSkip.LoadSpecific(br),
                Kind.HardSkip => HardSkip.LoadSpecific(br),
                Kind.Delete => Delete.LoadSpecific(br),
                Kind.Nested => Nested.LoadSpecific(br),
                Kind.DeleteWithoutChildren => DeleteWithoutChildren.LoadSpecific(br),
                _ => throw new InvalidDataException($"Unknown SlotModification type: {kind}"),
            };
        }
        public static SlotModification Load(String file_path)
        {
            using var fs = new FileStream(file_path, FileMode.Open, FileAccess.Read);
            var br = new BinaryReader(fs);
            return Load(br);
        }

        public abstract void WriteToConsoleContents(Int32 tabs);
        public void WriteToConsole(Int32 tabs)
        {
            for (var i = 0; i < tabs; i++)
                AnsiConsole.Markup(">\t");
            AnsiConsole.MarkupLineInterpolated($"[aqua]{this.GetType().Name}[/]");
            this.WriteToConsoleContents(tabs+1);
        }
        public void WriteToConsole() => this.WriteToConsole(1);

        private static class LinkOps
        {

            public static async Task<(List<ResoniteLink.Slot> children, List<Component> components)> GetSlotContents(String slot_path, LinkInterface link, ResoniteLink.Slot target)
            {
                //lock (Utils.OutLock)
                //    AnsiConsole.MarkupLineInterpolated($"[aqua]Getting slot contents at {slot_path}[/]");
                var r = await link.GetSlotData(new()
                {
                    Depth = 0,
                    SlotID = target.ID,
                    IncludeComponentData = false,
                });
                if (r.Success)
                    return (r.Data.Children ?? [], r.Data.Components);
                throw new InvalidOperationException($"Failed to get slot data at {slot_path}: {r.ErrorInfo}");
            }

            public static async Task DeleteSlot(String slot_path, LinkInterface link, ResoniteLink.Slot target)
            {
                var r = await link.RemoveSlot(new()
                {
                    SlotID = target.ID,
                });
                if (r.Success)
                    return;

                var (children, _) = await GetSlotContents(slot_path, link, new() { ID = target.Parent.TargetID });
                if (children.Any(child => child.ID == target.ID))
                {
                    lock (Utils.OutLock)
                        AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to delete slot at {slot_path} with error: {r.ErrorInfo}, but the slot is still there[/]");
                }
            }

            public static async Task DeleteComponent(String slot_path, LinkInterface link, ResoniteLink.Slot container, Component target)
            {
                var r = await link.RemoveComponent(new()
                {
                    ComponentID = target.ID,
                });
                if (r.Success)
                    return;

                var (_, components) = await GetSlotContents(slot_path, link, container);
                if (components.Any(component => component.ID == target.ID))
                {
                    lock (Utils.OutLock)
                        AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to delete component {target.ComponentType} at {slot_path} with error: {r.ErrorInfo}, but the component is still there[/]");
                }
            }

            public static async Task OverrideParentIfNotNull(String slot_path, LinkInterface link, ResoniteLink.Slot target, Reference? override_parent)
            {
                if (override_parent is null)
                    return;
                //TODO Doesn't work...
                // - How do I properly execute this?
                var r = await link.UpdateSlot(new()
                {
                    Data = new ResoniteLink.Slot()
                    {
                        ID = target.ID,
                        Parent = override_parent,
                    },
                });
                if (r.Success)
                    return;
                lock (Utils.OutLock)
                    AnsiConsole.MarkupLineInterpolated($"[yellow]Failed to override parent at {slot_path} with error: {r.ErrorInfo}[/]");
            }

        }

        public sealed class SoftSkip : SlotModification
        {
            public override async Task Apply(String slot_path, LinkInterface link, ResoniteLink.Slot target, Reference? override_parent) =>
                await LinkOps.OverrideParentIfNotNull(slot_path, link, target, override_parent);
            public override void Save(BinaryWriter bw)
            {
                bw.WriteEnum(Kind.SoftSkip);
                bw.Write(this.SlotDescr);
            }
            public static SoftSkip LoadSpecific(BinaryReader br) => new() { SlotDescr = br.ReadString() };
            public override void WriteToConsoleContents(Int32 tabs) { }
        }

        public sealed class HardSkip : SlotModification
        {
            public override async Task Apply(String slot_path, LinkInterface link, ResoniteLink.Slot target, Reference? override_parent) =>
                await LinkOps.OverrideParentIfNotNull(slot_path, link, target, override_parent);
            public override void Save(BinaryWriter bw)
            {
                bw.WriteEnum(Kind.HardSkip);
                bw.Write(this.SlotDescr);
            }
            public static HardSkip LoadSpecific(BinaryReader br) => new() { SlotDescr = br.ReadString() };
            public override void WriteToConsoleContents(Int32 tabs) { }
        }

        public sealed class Delete : SlotModification
        {
            public override async Task Apply(String slot_path, LinkInterface link, ResoniteLink.Slot target, Reference? override_parent) =>
                await LinkOps.DeleteSlot(slot_path, link, target);
            public override void Save(BinaryWriter bw)
            {
                bw.WriteEnum(Kind.Delete);
                bw.Write(this.SlotDescr);
            }
            public static Delete LoadSpecific(BinaryReader br) => new() { SlotDescr = br.ReadString() };
            public override void WriteToConsoleContents(Int32 tabs) { }
        }

        public sealed class Nested : SlotModification
        {
            public required SlotModification[] Children { get; init; }
            public required Boolean?[] ComponentDelete { get; init; }

            public override async Task Apply(String slot_path, LinkInterface link, ResoniteLink.Slot target, Reference? override_parent)
            {
                await LinkOps.OverrideParentIfNotNull(slot_path, link, target, override_parent);
                var (children, components) = await LinkOps.GetSlotContents(slot_path, link, target);

                if (children.Count != this.Children.Length)
                    throw new InvalidOperationException($"Children count mismatch at {slot_path}: expected {this.Children.Length}, actual {children.Count}");
                if (components.Count != this.ComponentDelete.Length)
                    throw new InvalidOperationException($"Components count mismatch at {slot_path}: expected {this.ComponentDelete.Length}, actual {components.Count}");

                foreach (var (child, mod) in children.Zip(this.Children))
                    await mod.Apply($"{slot_path} => {child.Name.Value} ({child.Tag.Value})", link, child, override_parent: null);
                foreach (var (component, delete) in components.Zip(this.ComponentDelete))
                {
                    if (delete != true)
                        continue;
                    await LinkOps.DeleteComponent(slot_path, link, target, component);
                }

            }

            public override void Save(BinaryWriter bw)
            {
                bw.WriteEnum(Kind.Nested);
                bw.Write(this.SlotDescr);
                bw.Write(this.Children.Length);
                foreach (var child in this.Children)
                    child.Save(bw);
                bw.Write(this.ComponentDelete.Length);
                foreach (var delete in this.ComponentDelete)
                {
                    bw.Write(delete.HasValue);
                    if (delete.HasValue)
                        bw.Write(delete.Value);
                }
            }

            public static Nested LoadSpecific(BinaryReader br)
            {
                var slot_descr = br.ReadString();
                var children_count = br.ReadInt32();
                var children = new SlotModification[children_count];
                for (Int32 i = 0; i < children_count; i++)
                    children[i] = Load(br);
                var component_delete_count = br.ReadInt32();
                var component_delete = new Boolean?[component_delete_count];
                for (Int32 i = 0; i < component_delete_count; i++)
                {
                    var has_value = br.ReadBoolean();
                    component_delete[i] = has_value ? br.ReadBoolean() : null;
                }
                return new Nested()
                {
                    SlotDescr = slot_descr,
                    Children = children,
                    ComponentDelete = component_delete,
                };
            }

            public override void WriteToConsoleContents(Int32 tabs)
            {
                foreach (var child in this.Children)
                    child.WriteToConsole(tabs);

                for (var i = 0; i < tabs; i++)
                    AnsiConsole.Markup(">\t");
                AnsiConsole.MarkupLineInterpolated($"[aqua]Components: {String.Join("", this.ComponentDelete.Select(x => x switch { true => 'd', false => 's', null => '?' }))}[/]");
            }
        }

        public sealed class DeleteWithoutChildren : SlotModification
        {
            public required SlotModification[] Children { get; init; }

            public override async Task Apply(String slot_path, LinkInterface link, ResoniteLink.Slot target, Reference? override_parent = null)
            {
                var (children, _) = await LinkOps.GetSlotContents(slot_path, link, target);

                if (children.Count != this.Children.Length)
                    throw new InvalidOperationException($"Children count mismatch at {slot_path}: expected {this.Children.Length}, actual {children.Count}");

                var next_parent = override_parent ?? target.Parent;
                foreach (var (child, mod) in children.Zip(this.Children))
                    await mod.Apply($"{slot_path} => {child.Name.Value} ({child.Tag.Value})", link, child, override_parent: next_parent);

                if (override_parent is null)
                    await LinkOps.DeleteSlot(slot_path, link, target);
            }

            public override void Save(BinaryWriter bw)
            {
                bw.WriteEnum(Kind.DeleteWithoutChildren);
                bw.Write(this.SlotDescr);
                bw.Write(this.Children.Length);
                foreach (var child in this.Children)
                    child.Save(bw);
            }

            public static DeleteWithoutChildren LoadSpecific(BinaryReader br)
            {
                var slot_descr = br.ReadString();
                var children_count = br.ReadInt32();
                var children = new SlotModification[children_count];
                for (Int32 i = 0; i < children_count; i++)
                    children[i] = Load(br);
                return new()
                {
                    SlotDescr = slot_descr,
                    Children = children,
                };
            }

            public override void WriteToConsoleContents(Int32 tabs)
            {
                foreach (var child in this.Children)
                    child.WriteToConsole(tabs);
            }
        }

    }


}
