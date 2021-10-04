﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EntityType = System.UInt32;

//TODO: cover with tests
namespace ECS
{
    static class EntityExtension
    {
        public const int BitSizeHalved = sizeof(EntityType) * 4;
        public const EntityType NullEntity = (1 << BitSizeHalved) - 1;
        
        static EntityExtension()
        {
            if (NullEntity.GetVersion() > 0)
                throw new EcsException("NullEntity should always have 0 version");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityType GetId(this EntityType entity)
        {
            var id = entity << BitSizeHalved;
            id >>= BitSizeHalved;
            return id;
        }

        public static void SetId(this ref EntityType entity, EntityType id)
        {
            if (id.GetId() >= NullEntity)
                throw new EcsException("set overflow id");
            entity = id.GetId() | entity.GetVersion();
        }

        public static void SetNullId(this ref EntityType entity)
        {
            entity = NullEntity.GetId() | entity.GetVersion();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNull(this in EntityType entity)
        {
            return entity.GetId() == NullEntity.GetId();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EntityType GetVersion(this EntityType entity)
        {
            return entity >> BitSizeHalved;
        }

        public static void IncrementVersion(this ref EntityType entity)
        {
            EntityType version = entity.GetVersion();
            version++;
            version <<= BitSizeHalved;
            entity = entity.GetId() | version;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToId(this EntityType entity) => (int)entity.GetId();

#region World methods forwarded
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T AddComponent<T>(this EntityType entity, EcsWorld world, T component = default)
            => ref world.AddComponent<T>(entity);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddTag<T>(this EntityType entity, EcsWorld world) => world.AddTag<T>(entity);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsDead(this EntityType entity, EcsWorld world) => world.IsDead(entity);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Have<T>(this EntityType entity, EcsWorld world)
            => world.Have<T>(entity);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetComponent<T>(this EntityType entity, EcsWorld world)
            => ref world.GetComponent<T>(entity);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RemoveComponent<T>(this EntityType entity, EcsWorld world)
            => world.RemoveComponent<T>(entity);

#endregion
    }

    class EcsException : Exception
    {
        public EcsException(string msg) : base(msg) { }
    }

    class EcsWorld
    {
        public EcsWorld(int entitiesReserved = 32)
        {
            _entites = new SimpleVector<EntityType>(entitiesReserved);
            _componentsPools = new Dictionary<Guid, IComponentsPool>();
        }

#region Entities methods
        private SimpleVector<EntityType> _entites;
        private EntityType _recycleListHead = EntityExtension.NullEntity;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsEnitityInRange(int id) => id < _entites.Length;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsEnitityInRange(EntityType entity) => IsEnitityInRange(entity.GetId());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref EntityType GetById(int id)
        {
            if (id == EntityExtension.NullEntity.GetId() || !IsEnitityInRange(id))
                throw new EcsException("wrong entity id");
            return ref _entites[id];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref EntityType GetById(EntityType other) => ref GetById(other.ToId());

        public bool IsDead(int id) => GetById(id).GetId() != id;

        public bool IsDead(EntityType entity) => IsDead(entity.ToId());

        private ref EntityType GetRecycled()
        {
            ref var curr = ref _recycleListHead;
            ref var next = ref GetById(curr);
            while (!next.IsNull())
            {
                curr = ref next;
                next = ref GetById(next);
            }

            next.SetId(curr);
            next.IncrementVersion();
            curr.SetNullId();
            return ref next;
        }

        public void Delete(EntityType entity)
        {
            if (IsDead(entity))
                throw new EcsException("trying to delete already dead entity");
            if (!IsEnitityInRange(entity))
                throw new EcsException("trying to delete wrong entity");
            if (entity.IsNull())
                throw new EcsException("trying to delete null entity");
            ref var recycleListEnd = ref _recycleListHead;
            while (!recycleListEnd.IsNull())
                recycleListEnd = ref GetById(recycleListEnd);
            recycleListEnd.SetId(entity);
            GetById(entity).SetNullId();
        }

        public ref EntityType Create()
        {
            if (!_recycleListHead.IsNull())
                return ref GetRecycled();

            var lastEntity = (EntityType)_entites.Length;
            //TODO: wrap all throws in ifdef
            if (lastEntity == EntityExtension.NullEntity)
                throw new EcsException("entity limit reached");
            if (_entites.Length < 0)
                throw new EcsException("entities vector length overflow");
            if (lastEntity.GetVersion() > 0)
                throw new EcsException("lastEntity version should always be 0");
            
            _entites.Add(lastEntity);
            return ref _entites[_entites.Length - 1];
        }
#endregion

#region Components methods
        private Dictionary<Guid, IComponentsPool> _componentsPools;

        //TODO: not sure about the way to store pools and get keys for them
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Guid TypeKey<T>() => default(T).GetType().GUID;

        public bool Have<T>(EntityType entity)
        {
            var key = TypeKey<T>();
            if (!_componentsPools.ContainsKey(key))
                return false;
            if (_componentsPools[key] as ComponentsPool<T> == null
                && _componentsPools[key] as TagsPool<T> == null)
            {
                throw new EcsException("invalid pool");
            }
            return _componentsPools[key].Contains(entity);
        }

        public ref T AddComponent<T>(EntityType entity, T component = default)
        {
            var key = TypeKey<T>();
            if (!_componentsPools.ContainsKey(key))
                _componentsPools.Add(key, new ComponentsPool<T>());
            var pool = _componentsPools[key] as ComponentsPool<T>;
            if (pool == null)
                throw new EcsException("invalid pool");
            return ref pool.Add(entity, component);
        }

        public void AddTag<T>(EntityType entity)
        {
            var key = TypeKey<T>();
            if (!_componentsPools.ContainsKey(key))
                _componentsPools.Add(key, new TagsPool<T>());
            var pool = _componentsPools[key] as TagsPool<T>;
            if (pool == null)
                throw new EcsException("invalid pool");
            pool.Add(entity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(EntityType entity)
        {
            var key = TypeKey<T>();
            var pool = _componentsPools[key] as ComponentsPool<T>;
            return ref pool[entity];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveComponent<T>(EntityType entity) => _componentsPools[TypeKey<T>()].Remove(entity);
#endregion
#region Filters methods
        //TODO: implement sortable groups
        public void GetView(ref SimpleVector<int> filter, in Type[] types, in Type[] excludes = null)
        {
            filter.Clear();
            if (types.Length == 0)
                return;

            var firstPool = _componentsPools[types[0].GUID];
            for (int i = 0; i < firstPool.Length; i++)
            {
                bool belongs = true;
                var id = firstPool.IthEntityId(i);
                if (IsDead(id))
                    continue;

                for (int j = 1; j < types.Length && belongs; j++)
                {
                    var pool = _componentsPools[types[j].GUID];
                    belongs &= pool.Contains(id);
                }
                for (int j = 0; excludes != null && j < excludes.Length && belongs; j++)
                {
                    var pool = _componentsPools[excludes[j].GUID];
                    belongs &= !pool.Contains(id);
                }
                if (belongs)
                    filter.Add(id);
            }
        }
#endregion
    }

    //usage example:
    class Program
    {
        struct Comp1 { public int i; }
        struct Comp2 { public float f; }
        struct Comp3 { public uint ui; }
        struct Tag1 { }
        struct Tag2 { }
        struct Tag3 { }

        static void Main(string[] args)
        {
            var world = new EcsWorld();

            ref var entity1 = ref world.Create();
            entity1.AddComponent<Comp1>(world).i = 10;
            entity1.AddComponent<Comp2>(world).f = 0.5f;
            entity1.AddTag<Tag1>(world);
            entity1.AddTag<Tag2>(world);

            ref var entity2 = ref world.Create();
            entity2.AddComponent<Comp1>(world).i = 10;
            entity2.AddComponent<Comp2>(world).f = 0.5f;
            entity2.AddTag<Tag2>(world);
            entity2.AddTag<Tag3>(world);

            ref var entity3 = ref world.Create();
            entity3.AddComponent<Comp1>(world).i = 10;
            entity3.AddComponent<Comp2>(world).f = 0.5f;
            entity3.AddTag<Tag1>(world);

            var filter = new SimpleVector<int>();

            Type GetType<T>() => default(T).GetType();

            var comps = new Type[] { GetType<Comp1>(), GetType<Comp2>(), GetType<Tag1>(), GetType<Tag2>() };
            var excludes = new Type[] { };
            world.GetView(ref filter, in comps, in excludes);//should be only 0

            comps = new Type[] { GetType<Comp2>() };
            excludes = new Type[] { };
            world.GetView(ref filter, in comps, in excludes);//should be all

            comps = new Type[] { GetType<Comp1>(), GetType<Comp2>(), GetType<Tag2>() };
            excludes = new Type[] { GetType<Tag1>() };
            world.GetView(ref filter, in comps, in excludes);//should be only 1

            for(int i = 0; i < filter.Length; i++)
            {
                var id = filter[i];
                ref var entity = ref world.GetById(id);
                var comp1 = entity.GetComponent<Comp1>(world);
                if (!entity.Have<Comp3>(world))
                {
                    //do smth
                    int a = 0;
                }
                if (entity.Have<Tag3>(world))
                {
                    //do smth
                    int a = 0;
                }
            }
        }
    }
}
