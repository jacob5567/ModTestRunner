using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Path = System.IO.Path;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using RoR2;
using RoR2.CameraModes;
using RoR2.UI;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;

namespace ModTestRunner
{
    // Runs scenarios one at a time and gives them the helpers to set the game up. A mod adds its own checks as extension
    // methods on Harness, built from Until, Holds, Fail and Diagnosis.
    //
    // The game is set up with DebugToolkit commands where one exists, and with the game's own server methods otherwise
    // (the runner only works as the host).
    public sealed class Harness
    {
        public const float DefaultTimeout = 10f;
        // How long Holds watches a condition by default, which is well past the frames a mod needs to react.
        public const float DefaultHoldSeconds = 1.5f;
        // Loading a stage takes far longer than any other wait.
        private const float SceneLoadTimeout = 120f;
        // After a stage loads the player is still landing in a drop pod, and the HUD is still attaching.
        private const float StageSettleSeconds = 4f;
        private const float AimSettleSeconds = 0.4f;
        private const float TeleportSettleSeconds = 0.5f;
        private const float SpotAhead = 7f;
        private const float StandDistance = 4f;
        // How far inside the player's interaction range to stand, and the least room to leave from an object's edge.
        private const float ReachMargin = 0.75f;
        private const float MinStandOff = 0.75f;
        // An object wider than this is stood before at this distance from its middle plus the stand-off.
        private const float MaxRadius = 3f;
        private const float GroundSearchAbove = 15f;
        private const float GroundSearchBelow = 40f;

        // Screenshots go in BepInEx/ModTestRunner/Screenshots/<when the run started>/.
        private const string ScreenshotFolderName = "Screenshots";
        private readonly string screenshotFolder = Path.Combine(Paths.BepInExRootPath, nameof(ModTestRunner), ScreenshotFolderName,
            DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        private int screenshotCount;

        // Where the last StandBefore put the player, for failure messages.
        private string lastStand = "never stood anywhere";

        // Where the camera was last pointed, so AimAway can turn from it.
        private Vector3 lastAimPoint;

        private static bool running;

        private readonly List<GameObject> spawned = new List<GameObject>();
        // What the scenario asked to have put back, run in reverse order by Cleanup.
        private readonly List<Action> cleanups = new List<Action>();
        private readonly HashSet<ConfigEntryBase> changedConfig = new HashSet<ConfigEntryBase>();
        private Exception error;
        private string currentScene;
        // The scenario in progress, whose mod readies stages, cleans up and explains failures.
        private Scenario scenario;

        public static CharacterBody LocalBody
        {
            get
            {
                NetworkUser user = LocalUser;
                return user ? user.GetCurrentBody() : null;
            }
        }

        private static NetworkUser LocalUser => NetworkUser.readOnlyLocalPlayersList.FirstOrDefault();

        // ---- Running the picked scenarios ----

        internal static void Start(List<Scenario> picked)
        {
            if (running)
            {
                ModTestRunnerPlugin.Log.LogWarning("A test run is already in progress.");
                return;
            }
            if (!Chainloader.PluginInfos.ContainsKey(Commands.DebugToolkitGUID))
            {
                ModTestRunnerPlugin.Log.LogError("DebugToolkit is not installed: the scenarios set the game up with its commands.");
                return;
            }

            running = true;
            ModTestRunnerPlugin.Instance.StartCoroutine(new Harness().RunScenarios(picked));
        }

        private IEnumerator RunScenarios(List<Scenario> list)
        {
            int passed = 0, failed = 0, skipped = 0;
            List<string> failures = new List<string>();
            ModTestRunnerPlugin.Log.LogInfo($"Running {list.Count} scenarios.");

            try
            {
                foreach (Scenario next in list)
                {
                    scenario = next;
                    string skip = Precondition(scenario);
                    if (skip != null)
                    {
                        skipped++;
                        ModTestRunnerPlugin.Log.LogWarning($"SKIP  {scenario.FullName}: {skip}");
                        continue;
                    }

                    float started = Time.realtimeSinceStartup;
                    error = null;
                    if (scenario.Scene != null && scenario.Scene != currentScene)
                    {
                        yield return Drive(LoadScene(scenario.Scene));
                        currentScene = error == null ? scenario.Scene : null;
                    }
                    if (error == null)
                    {
                        yield return Drive(scenario.Run(this));
                    }

                    string time = $"{Time.realtimeSinceStartup - started:0.0}s";
                    if (error == null)
                    {
                        passed++;
                        ModTestRunnerPlugin.Log.LogInfo($"PASS  {scenario.FullName} ({time})");
                    }
                    else if (error is ScenarioSkipped)
                    {
                        skipped++;
                        ModTestRunnerPlugin.Log.LogWarning($"SKIP  {scenario.FullName}: {error.Message}");
                    }
                    else
                    {
                        failed++;
                        string message = error is TestFailure ? error.Message : error.ToString();
                        failures.Add($"{scenario.FullName}: {message}");
                        ModTestRunnerPlugin.Log.LogError($"FAIL  {scenario.FullName} ({time}): {message}");
                    }

                    error = null;
                    yield return Drive(Cleanup());
                    if (error != null)
                    {
                        ModTestRunnerPlugin.Log.LogWarning($"Cleanup after {scenario.FullName} failed: {error.Message}");
                    }
                }

                string summary = $"Mod tests: {passed} passed, {failed} failed, {skipped} skipped.";
                ModTestRunnerPlugin.Log.LogInfo(summary);
                if (screenshotCount > 0)
                {
                    ModTestRunnerPlugin.Log.LogInfo($"Saved {screenshotCount} screenshots in {screenshotFolder}");
                }
                foreach (string failure in failures)
                {
                    ModTestRunnerPlugin.Log.LogError("  " + failure);
                }
                Debug.Log(summary + (failed > 0 ? " See the BepInEx log for the failures." : ""));
            }
            finally
            {
                running = false;
            }
        }

        // Why the scenario can't run now, or null.
        private static string Precondition(Scenario scenario)
        {
            try
            {
                if (!Run.instance) return "no run in progress";
                if (!LocalUser) return "no local user";
                return scenario.Mod.Precondition(scenario);
            }
            catch (Exception exception)
            {
                return "could not check: " + exception.Message;
            }
        }

        // Runs a coroutine, and ends it on the first exception, kept in 'error'. A yielded coroutine runs nested.
        private IEnumerator Drive(IEnumerator root)
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                object yielded;
                try
                {
                    if (!stack.Peek().MoveNext())
                    {
                        stack.Pop();
                        continue;
                    }
                    yielded = stack.Peek().Current;
                }
                catch (Exception exception)
                {
                    error = exception;
                    yield break;
                }

                if (yielded is IEnumerator nested)
                {
                    stack.Push(nested);
                }
                else
                {
                    yield return yielded;
                }
            }
        }

        private IEnumerator LoadScene(string scene)
        {
            Stage before = Stage.instance;
            ModTests mod = scenario.Mod;
            Cmd($"{Commands.NextStage} {scene}");
            yield return Until(() => Stage.instance && Stage.instance != before && StageName() == scene && LocalBody && mod.Ready(),
                () => StageName() == scene && LocalBody
                    ? $"{mod.Name} to be ready on {scene}"
                    : $"stage {scene} to load (in {StageName() ?? "no stage"})",
                SceneLoadTimeout);
            yield return Wait(StageSettleSeconds);
        }

        // Moves to another stage partway through a scenario, for one that has to have come from a particular stage. The
        // next scenario loads its own stage again.
        public IEnumerator GoTo(string scene)
        {
            currentScene = null;
            yield return LoadScene(scene);
            currentScene = scene;
        }

        private static string StageName()
        {
            SceneDef scene = SceneCatalog.GetSceneDefForCurrentScene();
            return scene ? scene.cachedName : null;
        }

        // What every scenario leaves behind: spawned objects, changed settings, items and monsters. Then the mod's own.
        private IEnumerator Cleanup()
        {
            foreach (GameObject spawn in spawned)
            {
                if (!spawn) continue;
                if (NetworkServer.active && spawn.GetComponent<NetworkIdentity>())
                {
                    NetworkServer.Destroy(spawn);
                }
                else
                {
                    UnityEngine.Object.Destroy(spawn);
                }
            }
            spawned.Clear();

            // Each runs even when one before it throws.
            for (int i = cleanups.Count - 1; i >= 0; i--)
            {
                try
                {
                    cleanups[i]();
                }
                catch (Exception exception)
                {
                    ModTestRunnerPlugin.Log.LogWarning($"A cleanup step after {scenario.FullName} failed: {exception.Message}");
                }
            }
            cleanups.Clear();
            changedConfig.Clear();

            Cmd(Commands.CleanInventory);
            Cmd(Commands.KillAll);
            yield return scenario.Mod.Cleanup(this);
            yield return Wait(TeleportSettleSeconds);
        }

        // ---- Setting the game up ----

        public void Cmd(string command)
        {
            NetworkUser user = LocalUser;
            if (!user) throw Fail("no local user to run commands as");

            RoR2.Console.instance.SubmitCmd(user, command);
        }

        public Exception Fail(string message)
        {
            return new TestFailure(message);
        }

        public Exception Skip(string reason)
        {
            return new ScenarioSkipped(reason);
        }

        // Runs after this scenario, before the mod's own Cleanup. Later ones run first.
        public void OnCleanup(Action restore)
        {
            cleanups.Add(restore);
        }

        // Changes a config setting for this scenario only.
        public void SetConfig<T>(ConfigEntry<T> entry, T value)
        {
            if (changedConfig.Add(entry))
            {
                T original = entry.Value;
                OnCleanup(() => entry.Value = original);
            }
            entry.Value = value;
        }

        public CharacterBody Body
        {
            get
            {
                CharacterBody body = LocalBody;
                if (!body) throw Fail("the local player has no living body");
                return body;
            }
        }

        public IEnumerator Wait(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
        }

        // Polls every frame until the condition holds. Fails after 'timeout' real seconds.
        public IEnumerator Until(Func<bool> condition, Func<string> what, float timeout = DefaultTimeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    throw Fail($"timed out after {timeout:0}s waiting for {what()}");
                }
                yield return null;
            }
        }

        public IEnumerator Until(Func<bool> condition, string what, float timeout = DefaultTimeout)
        {
            return Until(condition, () => what, timeout);
        }

        // The condition keeps holding for a while. Fails the moment it doesn't.
        public IEnumerator Holds(Func<bool> condition, Func<string> what, float seconds = DefaultHoldSeconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                if (!condition()) throw Fail($"expected {what()} to hold {Diagnosis()}");
                yield return null;
            }
        }

        // Runs a spawning command and hands back the first new component of type T. The object is destroyed in cleanup.
        public IEnumerator Spawned<T>(string command, Action<T> found, float timeout = DefaultTimeout) where T : Component
        {
            HashSet<T> before = new HashSet<T>(UnityEngine.Object.FindObjectsOfType<T>());
            Cmd(command);

            T result = null;
            yield return Until(() =>
            {
                foreach (T candidate in UnityEngine.Object.FindObjectsOfType<T>())
                {
                    if (!before.Contains(candidate))
                    {
                        result = candidate;
                        return true;
                    }
                }
                return false;
            }, () => $"a {typeof(T).Name} from \"{command}\"", timeout);

            spawned.Add(result.transform.root.gameObject);
            found(result);
        }

        // Spawns a prefab that no command spawns, in front of the player, on the server. Skips the scenario when the prefab
        // can't be loaded, since a wrong address is the likeliest reason.
        public T SpawnPrefab<T>(string address) where T : Component
        {
            GameObject prefab;
            try
            {
                prefab = Addressables.LoadAssetAsync<GameObject>(address).WaitForCompletion();
            }
            catch (Exception exception)
            {
                throw Skip($"could not load {address}: {exception.Message}");
            }
            if (!prefab) throw Skip($"{address} loaded as nothing");

            GameObject instance = UnityEngine.Object.Instantiate(prefab, GroundAhead(SpotAhead), Quaternion.identity);
            if (instance.GetComponent<NetworkIdentity>())
            {
                NetworkServer.Spawn(instance);
            }
            spawned.Add(instance);

            T component = instance.GetComponentInChildren<T>();
            if (!component) throw Fail($"{address} has no {typeof(T).Name}");
            return component;
        }

        // A pickup on the ground in front of the player. Made without the drop animation so it can't fall onto the player and
        // be picked up. The name is an item or equipment catalog name.
        public GenericPickupController SpawnPickup(string name)
        {
            PickupIndex index = PickupIndex.none;
            ItemIndex item = ItemCatalog.FindItemIndex(name);
            if (item != ItemIndex.None)
            {
                index = PickupCatalog.FindPickupIndex(item);
            }
            else
            {
                EquipmentIndex equipment = EquipmentCatalog.FindEquipmentIndex(name);
                if (equipment != EquipmentIndex.None)
                {
                    index = PickupCatalog.FindPickupIndex(equipment);
                }
            }
            if (index == PickupIndex.none) throw Fail($"no item or equipment called {name}");

            GenericPickupController.CreatePickupInfo info = new GenericPickupController.CreatePickupInfo
            {
                pickup = new UniquePickup(index),
                position = GroundAhead(SpotAhead) + Vector3.up,
                rotation = Quaternion.identity
            };
            GenericPickupController pickup = GenericPickupController.CreatePickup(in info);
            if (!pickup) throw Fail($"could not create a pickup of {name}");

            spawned.Add(pickup.gameObject);
            return pickup;
        }

        // The catalog name of the first usable item of a tier that the run has enabled, or null (for example the Meal tier
        // without DLC3). Items 'skip' accepts are passed over.
        public static string ItemOfTier(ItemTier tier, Func<ItemDef, bool> skip = null)
        {
            foreach (ItemDef item in ItemCatalog.allItemDefs)
            {
                if (item.tier != tier || item.hidden || item.isConsumed) continue;
                if (skip != null && skip(item)) continue;
                if (item.requiredExpansion && Run.instance && !Run.instance.IsExpansionEnabled(item.requiredExpansion)) continue;
                return item.name;
            }
            return null;
        }

        private Vector3 GroundAhead(float distance)
        {
            CharacterBody body = Body;
            Vector3 forward = Vector3.ProjectOnPlane(body.inputBank ? body.inputBank.aimDirection : body.transform.forward, Vector3.up).normalized;
            Vector3 spot = body.footPosition + forward * distance;
            return Physics.Raycast(spot + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 30f, LayerIndex.world.mask)
                ? hit.point
                : spot;
        }

        // Moves the player to a spot with a clear view of the object, then looks at it. The distance is from the object's
        // edge, capped so the player stays within interaction range: the game only outlines what that range reaches. The
        // edge and the aim point come from the colliders the game's interaction check hits for this object.
        public IEnumerator StandBefore(GameObject obj, float distance = StandDistance)
        {
            return StandBeforeObject(obj, distance);
        }

        private IEnumerator StandBeforeObject(GameObject obj, float distance)
        {
            Bounds bounds = InteractBounds(obj);
            Interactor interactor = Body.GetComponent<Interactor>();
            float reach = interactor ? interactor.maxInteractionDistance : distance;
            float standOff = Mathf.Max(Mathf.Min(distance, reach - ReachMargin), MinStandOff);
            yield return StandBefore(bounds.center, obj.transform, obj.name, Mathf.Min(Radius(bounds), MaxRadius) + standOff, bounds);
            if (interactor) yield return CloseIn(obj, bounds.center, reach);
        }

        // The game only outlines what the crosshair ray reaches within the interaction distance, so a wide object can still
        // be out of reach from where the player stands. Steps toward it until the ray meets it in range.
        private IEnumerator CloseIn(GameObject obj, Vector3 target, float reach)
        {
            const int Steps = 6;
            for (int i = 0; i < Steps; i++)
            {
                CharacterBody body = Body;
                Ray ray = new Ray(body.inputBank.aimOrigin, body.inputBank.aimDirection);
                float hitDistance = float.MaxValue;
                foreach (RaycastHit hit in Physics.RaycastAll(ray, 50f, LayerIndex.CommonMasks.interactable, QueryTriggerInteraction.Collide))
                {
                    if (EntityLocator.HasEntityLocator(hit.collider.gameObject, out EntityLocator locator) && locator.entity == obj)
                    {
                        hitDistance = Mathf.Min(hitDistance, hit.distance);
                    }
                }
                if (hitDistance <= reach - ReachMargin) yield break;

                Vector3 toward = Vector3.ProjectOnPlane(target - body.footPosition, Vector3.up);
                if (toward.magnitude < 0.5f) yield break;
                float step = hitDistance < float.MaxValue ? hitDistance - (reach - ReachMargin) + 0.25f : 1f;
                Vector3 next = body.footPosition + toward.normalized * Mathf.Min(step, toward.magnitude - 0.5f);
                if (Physics.Raycast(next + Vector3.up * 3f, Vector3.down, out RaycastHit ground, 10f, LayerIndex.world.mask))
                {
                    next = ground.point + Vector3.up * 0.2f;
                }
                Teleport(next);
                lastStand = $"stood {Vector3.Distance(next, target):0.0} m from {obj.name} after closing in";
                yield return Wait(TeleportSettleSeconds);
                yield return AimAt(target);
            }
        }

        public IEnumerator StandBefore(CharacterBody monster, float distance = StandDistance)
        {
            return StandBefore(monster.corePosition, monster.transform, monster.name, monster.radius + distance,
                new Bounds(monster.corePosition, Vector3.one * monster.radius * 2f));
        }

        // Moves the player near a point in the world, for things that switch on when a player arrives (the Newt).
        public IEnumerator StandNear(Vector3 point, string name, float distance = StandDistance)
        {
            return StandBefore(point, null, name, distance, new Bounds(point, Vector3.zero));
        }

        // Tries spots around the target, nearest first, until one has ground under it and a clear view of the target.
        private IEnumerator StandBefore(Vector3 target, Transform owner, string name, float distance, Bounds space)
        {
            const int Directions = 16;
            float[] distances = { distance, distance + 2f, distance + 4f };
            string blocker = "no ground under any spot";
            foreach (float d in distances)
            {
                for (int i = 0; i < Directions; i++)
                {
                    Vector3 candidate = target + Quaternion.Euler(0f, i * 360f / Directions, 0f) * Vector3.forward * d;
                    // A tall search, so the ground is found under something hovering high (the Alloy Worship Unit).
                    if (!Physics.Raycast(candidate + Vector3.up * GroundSearchAbove, Vector3.down, out RaycastHit ground,
                            GroundSearchAbove + GroundSearchBelow, LayerIndex.world.mask)) continue;

                    Vector3 feet = ground.point + Vector3.up * 0.2f;
                    string blockedBy = BlockedBy(feet + Vector3.up * 1.6f, target, owner, space);
                    if (blockedBy != null)
                    {
                        blocker = blockedBy;
                        continue;
                    }

                    Teleport(feet);
                    lastStand = $"stood {Vector3.Distance(feet, target):0.0} m from {name}";
                    yield return Wait(TeleportSettleSeconds);
                    yield return AimAt(target);
                    yield break;
                }
            }
            throw Fail($"no clear spot {distance:0.0} to {distance + 4f:0.0} m from {name}: the view was blocked by {blocker}");
        }

        // What blocks a view of the target, or null. Ignored: the object's own colliders, which a line to its middle always
        // crosses, and anything within the object's own space (its stand, the ground it sits on), with a meter to spare.
        private static string BlockedBy(Vector3 eye, Vector3 target, Transform owner, Bounds space)
        {
            Bounds around = space;
            around.Expand(2f);
            Vector3 toTarget = target - eye;
            foreach (RaycastHit hit in Physics.RaycastAll(eye, toTarget.normalized, toTarget.magnitude, LayerIndex.world.mask))
            {
                if (owner && hit.collider.transform.IsChildOf(owner)) continue;
                if (around.Contains(hit.point)) continue;
                return hit.collider.name;
            }
            return null;
        }

        // The colliders whose EntityLocator names this object, which is what the game's interaction raycast and overlap
        // sphere go by: solid ones first, since a portal or shrine can carry a trigger volume many meters wide. A
        // Primordial Teleporter's prongs have their own entity, so they don't count as the teleporter.
        private static Bounds InteractBounds(GameObject obj)
        {
            if (TryEntityBounds(obj, false, out Bounds solid)) return solid;
            if (TryEntityBounds(obj, true, out Bounds triggers)) return triggers;
            return new Bounds(CenterOf(obj), Vector3.zero);
        }

        private static bool TryEntityBounds(GameObject obj, bool triggers, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Collider collider in obj.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger != triggers) continue;
                if (!EntityLocator.HasEntityLocator(collider.gameObject, out EntityLocator locator) || locator.entity != obj) continue;
                if (any)
                {
                    bounds.Encapsulate(collider.bounds);
                }
                else
                {
                    bounds = collider.bounds;
                    any = true;
                }
            }
            return any;
        }

        private static float Radius(Bounds bounds)
        {
            return Mathf.Max(bounds.extents.x, bounds.extents.z);
        }

        // The two-argument overload is marked obsolete, but the replacement takes an args struct built by the game's
        // own callers; the simple form still works and keeps the body's rotation.
        public void Teleport(Vector3 footPosition)
        {
#pragma warning disable CS0618
            TeleportHelper.TeleportBody(Body, footPosition);
#pragma warning restore CS0618
        }

        public IEnumerator AimAt(GameObject obj)
        {
            return AimAt(InteractBounds(obj).center);
        }

        public IEnumerator AimAt(Vector3 point)
        {
            CharacterBody body = Body;
            CameraRigController rig = LocalUser.cameraRigController;
            if (!rig || !body.inputBank) throw Fail("the player has no camera");

            // The look direction lives in the camera mode's per-camera data; the rig has no setter of its own.
            CameraModePlayerBasic.InstanceData camera = rig.cameraMode is CameraModePlayerBasic mode
                ? mode.DebugGetInstanceData(rig) as CameraModePlayerBasic.InstanceData
                : null;
            if (camera == null) throw Fail("the camera is not the player camera mode");

            lastAimPoint = point;
            camera.SetPitchYawFromLookVector((point - body.inputBank.aimOrigin).normalized);
            yield return Wait(AimSettleSeconds);
        }

        // Turns the player's back on the last thing aimed at, looking a little down. With nothing under the crosshair the
        // game still outlines anything in reach that is less than 90 degrees off the aim (Interactor's overlap sphere), so
        // looking up past a tall object isn't enough.
        public IEnumerator AimAway()
        {
            Vector3 origin = Body.inputBank.aimOrigin;
            Vector3 away = Vector3.ProjectOnPlane(origin - lastAimPoint, Vector3.up);
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            return AimAt(origin + away.normalized * 10f + Vector3.down * 4f);
        }

        public IEnumerator AimAt(CharacterBody target)
        {
            return AimAt(target.corePosition);
        }

        // The first monster body of this catalog name, or null.
        public CharacterBody FindBody(string bodyName)
        {
            BodyIndex index = BodyCatalog.FindBodyIndex(bodyName);
            return CharacterBody.readOnlyInstancesList.FirstOrDefault(body => body && body.bodyIndex == index);
        }

        // The middle of the object's solid colliders. Portals, shrines and chests are often only triggers, so those count
        // next, then what is drawn. The last resort is a little above the pivot, which sits on the ground.
        private static Vector3 CenterOf(GameObject obj)
        {
            if (TryBounds(obj, false, out Bounds solid)) return solid.center;
            if (TryBounds(obj, true, out Bounds triggers)) return triggers.center;

            Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                Bounds drawn = renderers[0].bounds;
                foreach (Renderer renderer in renderers) drawn.Encapsulate(renderer.bounds);
                return drawn.center;
            }
            return obj.transform.position + Vector3.up;
        }

        private static bool TryBounds(GameObject obj, bool triggers, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Collider collider in obj.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger != triggers) continue;
                if (any)
                {
                    bounds.Encapsulate(collider.bounds);
                }
                else
                {
                    bounds = collider.bounds;
                    any = true;
                }
            }
            return any;
        }

        // The player uses the interactable, as the E key would. They must stand close.
        public IEnumerator Interact(GameObject obj)
        {
            yield return StandBefore(obj, 2f);
            Interactor interactor = Body.GetComponent<Interactor>();
            if (!interactor) throw Fail("the player body has no Interactor");

            interactor.AttemptInteraction(obj);
            yield return Wait(AimSettleSeconds);
        }

        public void SetHealthFraction(CharacterBody body, float fraction)
        {
            body.healthComponent.Networkhealth = body.healthComponent.fullHealth * fraction;
        }

        // ---- Screenshots ----

        // Saves the screen as it is drawn this frame, as a PNG named after the scenario and the label, in this run's
        // screenshot folder. The console is hidden for that frame, since it is usually open from starting the run. Not a
        // check: give the game time to show what the screenshot is of first.
        public IEnumerator Screenshot(string label = null)
        {
            List<Canvas> hidden = new List<Canvas>();
            if (ConsoleWindow.instance)
            {
                foreach (Canvas canvas in ConsoleWindow.instance.transform.root.GetComponentsInChildren<Canvas>())
                {
                    if (!canvas.enabled) continue;
                    canvas.enabled = false;
                    hidden.Add(canvas);
                }
            }

            // The frame is captured once it is drawn, so the console is already hidden in it.
            yield return new WaitForEndOfFrame();
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                Directory.CreateDirectory(screenshotFolder);
                string name = scenario != null ? scenario.FullName : "screenshot";
                if (!string.IsNullOrEmpty(label))
                {
                    name += "." + label;
                }
                foreach (char invalid in Path.GetInvalidFileNameChars())
                {
                    name = name.Replace(invalid, '_');
                }
                string path = Path.Combine(screenshotFolder, name + ".png");
                File.WriteAllBytes(path, shot.EncodeToPNG());
                screenshotCount++;
                ModTestRunnerPlugin.Log.LogInfo($"Screenshot: {path}");
            }
            finally
            {
                UnityEngine.Object.Destroy(shot);
                foreach (Canvas canvas in hidden)
                {
                    if (canvas)
                    {
                        canvas.enabled = true;
                    }
                }
            }
        }

        // ---- Explaining failures ----

        // What the mod and the runner saw, to add to a failure message: the mod's Diagnose, where the player last stood and
        // what the crosshair is on.
        public string Diagnosis()
        {
            string mod;
            try
            {
                mod = scenario?.Mod.Diagnose();
            }
            catch (Exception exception)
            {
                mod = "could not describe: " + exception.Message;
            }
            return (mod != null ? "\n    mod: " + mod : "")
                + $"\n    runner: {lastStand}; the crosshair ray hits {AimedCollider()}";
        }

        // What the game's interaction raycast would hit along the crosshair: the collider and the entity it belongs to.
        private static string AimedCollider()
        {
            CharacterBody body = LocalBody;
            if (!body || !body.inputBank) return "nothing (no body)";

            Ray ray = new Ray(body.inputBank.aimOrigin, body.inputBank.aimDirection);
            if (!Physics.Raycast(ray, out RaycastHit hit, 100f, LayerIndex.CommonMasks.interactable | LayerIndex.world.mask, QueryTriggerInteraction.Collide))
            {
                return "nothing within 100 m";
            }
            string entity = EntityLocator.HasEntityLocator(hit.collider.gameObject, out EntityLocator locator) && locator.entity
                ? locator.entity.name
                : "no entity";
            return $"{hit.collider.name} ({LayerMask.LayerToName(hit.collider.gameObject.layer)}, {entity}) at {hit.distance:0.0} m";
        }
    }
}
