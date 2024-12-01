﻿#pragma warning disable CS0626 // Method, operator, or accessor is marked external and has no attributes on it

using Celeste.Mod;
using Celeste.Mod.Helpers;
using MonoMod;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Monocle {
    /// <summary>
    /// When applied on an entity or component, this attribute makes the entity tracked the same way as another entity or component.
    /// </summary>
    public class TrackedAsAttribute : Attribute {
        public Type TrackedAsType;
        public bool Inherited;

        /// <summary>
        /// Makes this entity/component tracked the same way as another entity/component.<br/>
        /// It can then be accessed through <see cref="Tracker.GetEntities{T}"/> or <see cref="Tracker.GetComponents{T}"/> with the generic param of <paramref name="trackedAsType"/>.
        /// </summary>
        /// <param name="trackedAsType">Type to track this entity/component as.</param>
        public TrackedAsAttribute(Type trackedAsType) {
            TrackedAsType = trackedAsType;
        }

        /// <inheritdoc cref="TrackedAsAttribute(Type)"/>
        /// <param name="trackedAsType">Type to track this entity/component as.</param>
        /// <param name="inherited">Whether all child classes should also be tracked as <paramref name="trackedAsType"/>.</param>
        public TrackedAsAttribute(Type trackedAsType, bool inherited = false) {
            TrackedAsType = trackedAsType;
            Inherited = inherited;
        }
    }

    class patch_Tracker : Tracker {
        // A temporary cache to store the results of FakeAssembly.GetFakeEntryAssemblies
        // Set to null outside of Initialize.
        private static Type[] _temporaryAllTypes;

        private static Type[] GetAllTypesUncached() => FakeAssembly.GetFakeEntryAssembly().GetTypesSafe();
        
        [MonoModReplace]
        private static List<Type> GetSubclasses(Type type) {
            bool shouldNullOutCache = _temporaryAllTypes is null;
            _temporaryAllTypes ??= GetAllTypesUncached();
            
            List<Type> subclasses = new();
            foreach (Type otherType in _temporaryAllTypes)
            {
                if (type != otherType && type.IsAssignableFrom(otherType))
                    subclasses.Add(otherType);
            }

            // This method got called outside of Initialize, so we can't rely on it clearing out the cache.
            // Let's do that now instead.
            if (shouldNullOutCache)
                _temporaryAllTypes = null;
            
            return subclasses;
        }

        public static extern void orig_Initialize();
        public new static void Initialize() {
            _temporaryAllTypes = GetAllTypesUncached();
            
            orig_Initialize();

            // search for entities with [TrackedAs]
            foreach (Type type in _temporaryAllTypes) {
                object[] customAttributes = type.GetCustomAttributes(typeof(TrackedAsAttribute), inherit: false);
                foreach (object customAttribute in customAttributes) {
                    TrackedAsAttribute trackedAs = customAttribute as TrackedAsAttribute;
                    Type trackedAsType = trackedAs.TrackedAsType;
                    bool inherited = trackedAs.Inherited;
                    if (typeof(Entity).IsAssignableFrom(type)) {
                        if (!type.IsAbstract) {
                            // this is an entity. copy the registered types for the target entity
                            if (!TrackedEntityTypes.ContainsKey(type)) {
                                TrackedEntityTypes.Add(type, new List<Type>());
                            }
                            TrackedEntityTypes[type].AddRange(TrackedEntityTypes.TryGetValue(trackedAsType, out List<Type> list) ? list : new List<Type>());
                            TrackedEntityTypes[type] = TrackedEntityTypes[type].Distinct().ToList();
                        }
                        if (inherited) {
                            // do the same for subclasses
                            foreach (Type subclass in GetSubclasses(type)) {
                                if (!subclass.IsAbstract) {
                                    if (!TrackedEntityTypes.ContainsKey(subclass))
                                        TrackedEntityTypes.Add(subclass, new List<Type>());
                                    TrackedEntityTypes[subclass].AddRange(TrackedEntityTypes.TryGetValue(trackedAsType, out List<Type> list) ? list : new List<Type>());
                                    TrackedEntityTypes[subclass] = TrackedEntityTypes[subclass].Distinct().ToList();
                                }
                            }
                        }
                    } else if (typeof(Component).IsAssignableFrom(type)) {
                        if (!type.IsAbstract) {
                            // this is an component. copy the registered types for the target component
                            if (!TrackedComponentTypes.ContainsKey(type)) {
                                TrackedComponentTypes.Add(type, new List<Type>());
                            }
                            TrackedComponentTypes[type].AddRange(TrackedComponentTypes.TryGetValue(trackedAsType, out List<Type> list) ? list : new List<Type>());
                            TrackedComponentTypes[type] = TrackedComponentTypes[type].Distinct().ToList();
                        }
                        if (inherited) {
                            // do the same for subclasses
                            foreach (Type subclass in GetSubclasses(type)) {
                                if (!subclass.IsAbstract) {
                                    if (!TrackedComponentTypes.ContainsKey(subclass))
                                        TrackedComponentTypes.Add(subclass, new List<Type>());
                                    TrackedComponentTypes[subclass].AddRange(TrackedComponentTypes.TryGetValue(trackedAsType, out List<Type> list) ? list : new List<Type>());
                                    TrackedComponentTypes[subclass] = TrackedComponentTypes[subclass].Distinct().ToList();
                                }
                            }
                        }
                    } else {
                        // this is neither an entity nor a component. Help!
                        throw new Exception("Type '" + type.Name + "' cannot be TrackedAs because it does not derive from Entity or Component");
                    }
                }
            }

            // don't hold references to all the types anymore
            _temporaryAllTypes = null;
        }

        public static void AddTypeToTracker(Type type, Type trackedAs = null, bool inheritAll = false) {
            AddTypeToTracker(type, trackedAs, inheritAll ? GetSubclasses(type).ToArray() : Array.Empty<Type>());
        }

        public static void AddTypeToTracker(Type type, Type trackedAs = null, params Type[] subtypes) {
            if (typeof(Entity).IsAssignableFrom(type)) {
                StoredEntityTypes.Add(type);
                if (!type.IsAbstract) {
                    if (!TrackedEntityTypes.TryGetValue(type, out List<Type> value)) {
                        value = new List<Type>();
                        TrackedEntityTypes.Add(type, value);
                    }
                    value.AddRange(TrackedEntityTypes.TryGetValue(trackedAs != null && trackedAs.IsAssignableFrom(type)
                        ? trackedAs : type, out List<Type> list) ? list : new List<Type>());
                    TrackedEntityTypes[type] = value.Distinct().ToList();
                }
                foreach (Type subtype in subtypes) {
                    if (!subtype.IsAbstract) {
                        if (!TrackedEntityTypes.TryGetValue(subtype, out List<Type> value)) {
                            value = new List<Type>();
                            TrackedEntityTypes.Add(subtype, value);
                        }
                        value.AddRange(TrackedEntityTypes.TryGetValue(trackedAs != null && trackedAs.IsAssignableFrom(type)
                            ? trackedAs : type, out List<Type> list) ? list : new List<Type>());
                        TrackedEntityTypes[subtype] = value.Distinct().ToList();
                    }
                }
            }
            else if (typeof(Component).IsAssignableFrom(type)) {
                StoredComponentTypes.Add(type);
                if (!type.IsAbstract) {
                    if (!TrackedComponentTypes.TryGetValue(type, out List<Type> value)) {
                        value = new List<Type>();
                        TrackedComponentTypes.Add(type, value);
                    }
                    value.AddRange(TrackedComponentTypes.TryGetValue(trackedAs != null && trackedAs.IsAssignableFrom(type)
                        ? trackedAs : type, out List<Type> list) ? list : new List<Type>());
                    TrackedComponentTypes[type] = value.Distinct().ToList();
                }
                foreach(Type subtype in subtypes) {
                    if (!subtype.IsAbstract) {
                        if (!TrackedComponentTypes.TryGetValue(subtype, out List<Type> value)) {
                            value = new List<Type>();
                            TrackedComponentTypes.Add(subtype, value);
                        }
                        value.AddRange(TrackedComponentTypes.TryGetValue(trackedAs != null && trackedAs.IsAssignableFrom(type)
                            ? trackedAs : type, out List<Type> list) ? list : new List<Type>());
                        TrackedComponentTypes[subtype] = value.Distinct().ToList();
                    }
                }
            }
            else {
                throw new Exception("Type '" + type.Name + "' cannot be TrackedAs because it does not derive from Entity or Component");
            }
            RefreshTracker(type);
        }

        public static void RefreshTracker(Type type) {
            if (typeof(Entity).IsAssignableFrom(type) && !Engine.Scene.Tracker.Entities.ContainsKey(type)) {
                Engine.Scene.Tracker.Entities.Add(type, new List<Entity>());
            }
            else if (typeof(Component).IsAssignableFrom(type) && !Engine.Scene.Tracker.Components.ContainsKey(type)) {
                Engine.Scene.Tracker.Components.Add(type, new List<Component>());
            } else {
                throw new Exception("Type '" + type.Name + "' does not derive from Entity or Component");
            }
            RefreshTrackerLists();
        }

        public static void RefreshTracker() {
            foreach (Type entityType in StoredEntityTypes) {
                if (!Engine.Scene.Tracker.Entities.ContainsKey(entityType)) {
                    Engine.Scene.Tracker.Entities.Add(entityType, new List<Entity>());
                }
            }
            foreach (Type componentType in StoredComponentTypes) {
                if (!Engine.Scene.Tracker.Components.ContainsKey(componentType)) {
                    Engine.Scene.Tracker.Components.Add(componentType, new List<Component>());
                }
            }
            RefreshTrackerLists();
        }

        private static void RefreshTrackerLists() {
            foreach (Entity entity in Engine.Scene.Entities) {
                foreach (Component component in entity.Components) {
                    Type componentType = component.GetType();
                    if (!TrackedComponentTypes.TryGetValue(componentType, out List<Type> componentTypes)) {
                        continue;
                    }
                    foreach (Type trackedType in componentTypes) {
                        Engine.Scene.Tracker.Components[trackedType].Add(component);
                    }
                }
                Type entityType = entity.GetType();
                if (!TrackedEntityTypes.TryGetValue(entityType, out List<Type> entityTypes)) {
                    continue;
                }
                foreach (Type trackedType in entityTypes) {
                    Engine.Scene.Tracker.Entities[trackedType].Add(entity);
                }
            }
        }
    }
}
