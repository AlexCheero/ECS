﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EntityType = System.Int32;

#if DEBUG
using System.Text;
#endif

//TODO: cover with tests
namespace ECS
{
    //TODO: think about implementing dynamically counted initial size
    public static class EcsCacheSettings
    {
        public static int UpdateSetSize = 4;
        public static int PoolSize = 512;
        public static int FilteredEntitiesSize = 128;
        public static int PoolsCount = 16;
    }

    class EcsException : Exception
    {
        public EcsException(string msg) : base(msg) { }
    }

    public class EcsWorld
    {
        private SimpleVector<EntityType> _entites;
        private EntityType _recycleListHead = EntityExtension.NullEntity;

        private SimpleVector<BitMask> _masks;

        private SparseSet<IComponentsPool> _componentsPools;

        //update sets holds indices of filters by types
        private Dictionary<int, HashSet<int>> _includeUpdateSets;
        private Dictionary<int, HashSet<int>> _excludeUpdateSets;
        private FiltersCollection _filtersCollection;

        public EcsWorld(int entitiesReserved = 32)
        {
            //TODO: ensure that _entities and masks are always have same length
            _entites = new SimpleVector<EntityType>(entitiesReserved);
            _masks = new SimpleVector<BitMask>(entitiesReserved);
            _componentsPools = new SparseSet<IComponentsPool>(EcsCacheSettings.PoolsCount);
            
            _includeUpdateSets = new Dictionary<int, HashSet<int>>();
            _excludeUpdateSets = new Dictionary<int, HashSet<int>>();
            _filtersCollection = new FiltersCollection();
        }

        //prealloc ctor
        public EcsWorld(EcsWorld other)
        {
            _entites = new SimpleVector<EntityType>(other._entites.Reserved);
            _masks = new SimpleVector<BitMask>(other._masks.Reserved);
            _componentsPools = new SparseSet<IComponentsPool>(other._componentsPools.Length);

            //update sets should be same for every copy of the world
            _includeUpdateSets = other._includeUpdateSets;
            _excludeUpdateSets = other._excludeUpdateSets;
            _filtersCollection = new FiltersCollection(other._filtersCollection.Length);
        }

        public void Copy(in EcsWorld other)
        {
            _entites.Copy(other._entites);
            _recycleListHead = other._recycleListHead;
            _masks.Copy(other._masks);

            var length = other._componentsPools.Length;
            var dense = other._componentsPools._dense;
            for (int i = 0; i < length; i++)
            {
                var compId = dense[i];
                var otherPool = other._componentsPools[compId];
                if (_componentsPools.Contains(compId))
                    _componentsPools[compId].Copy(otherPool);
                else
                    _componentsPools.Add(compId, otherPool.Duplicate());
            }

            if (length < _componentsPools.Length)
            {
                for (int i = 0; i < _componentsPools.Length; i++)
                {
                    var compId = _componentsPools._dense[i];
                    if (!other._componentsPools.Contains(compId))
                        _componentsPools[compId].Clear();
                }
            }

            _filtersCollection.Copy(other._filtersCollection);
        }

        public byte[] Serialize()
        {
            int size = 2 * sizeof(int);/*_entites.Length and _entites._elements.Length*/
            size += sizeof(EntityType) * _entites.Length;
            size += sizeof(EntityType);/*_recycleListHead*/
            size += 2 * sizeof(int);/*_masks.Length and _masks._elements.Length*/
            for (int i = 0; i < _masks.Length; i++)
                size += _masks[i].ByteLength;
            size += sizeof(int) + _componentsPools._sparse.Length * sizeof(int);
            size += 2 * sizeof(int);/*_componentsPools._values.Length and _componentsPools._values._elements.Length*/
            for (int i = 0; i < _componentsPools._values.Length; i++)
                size += _componentsPools._values[i].ByteLength();
            size += 2 * sizeof(int);/*_componentsPools._dense.Length and _componentsPools._dense._elements.Length*/
            size += sizeof(EntityType) * _componentsPools._dense.Length;
            size += sizeof(int) + sizeof(int) * _includeUpdateSets.Count;
            foreach (var pair in _includeUpdateSets)
            {
                size += sizeof(int);
            }
            //size += sizeof(int) + sizeof(int) * _includeUpdateSets.Count;
            //size += sizeof(int) + sizeof(int) * _excludeUpdateSets.Count;
            return null;
        }

        public void Deserialize(byte[] bytes)
        {

        }

#region Entities methods

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsEnitityInRange(int id) => id < _entites.Length;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref EntityType GetRefById(int id)
        {
#if DEBUG
            if (id == EntityExtension.NullEntity.GetId() || !IsEnitityInRange(id))
                throw new EcsException("wrong entity id");
#endif
            return ref _entites[id];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityType GetById(int id) => GetRefById(id);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsDead(int id) => GetRefById(id).GetId() != id;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsNull(int id) => GetById(id).IsNull();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(int entity1, int entity2)
        {
            if (entity1 == entity2)
                return GetById(entity1).GetVersion() == GetById(entity2).GetVersion();
            else
                return false;
        }

        private int GetRecycledId()
        {
            ref var curr = ref _recycleListHead;
            ref var next = ref GetRefById(curr);
            while (!next.IsNull())
            {
                curr = ref next;
                next = ref GetRefById(next);
            }

            next.SetId(curr.ToId());
            next.IncrementVersion();
            curr.SetNullId();
            return next.ToId();
        }

        public void Delete(int id)
        {
            ref EntityType entity = ref GetRefById(id);
#if DEBUG
            if (IsDead(id))
                throw new EcsException("trying to delete already dead entity");
            if (!IsEnitityInRange(id))
                throw new EcsException("trying to delete wrong entity");
            if (entity.IsNull())
                throw new EcsException("trying to delete null entity");
#endif
            var mask = _masks[id];
            var nextSetBit = mask.GetNextSetBit(0);
            while (nextSetBit != -1)
            {
                RemoveComponent(id, nextSetBit);
                nextSetBit = mask.GetNextSetBit(nextSetBit + 1);
            }

            _filtersCollection.RemoveId(id);

            ref var recycleListEnd = ref _recycleListHead;
            while (!recycleListEnd.IsNull())
                recycleListEnd = ref GetRefById(recycleListEnd);
            recycleListEnd.SetId(id);
            entity.SetNullId();
        }

        public int Create()
        {
            if (!_recycleListHead.IsNull())
                return GetRecycledId();

            var lastEntity = _entites.Length;
#if DEBUG
            if (lastEntity == EntityExtension.NullEntity)
                throw new EcsException("entity limit reached");
            if (_entites.Length < 0)
                throw new EcsException("entities vector length overflow");
            if (lastEntity.GetVersion() > 0)
                throw new EcsException("lastEntity version should always be 0");
#endif

            _entites.Add(lastEntity);
            _masks.Add(new BitMask());//TODO: precache with _registeredComponents.Count
            return _entites.Length - 1;
        }
#endregion

#region Components methods
        //TODO: add reactive callbacks

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Have<T>(int id) => _masks[id].Check(ComponentMeta<T>.Id);

        private void AddIdToFlters(int id, HashSet<int> filterIds)
        {
            foreach (var filterId in filterIds)
            {
                var filter = _filtersCollection[filterId];

                var pass = _masks[id].InclusivePass(filter.Includes);
                pass &= _masks[id].ExclusivePass(filter.Excludes);
                if (!pass)
                    continue;
                //could try to add same id several times due to delayed set modification operations
                filter.Add(id);
            }
        }

        private void RemoveIdFromFilters(int id, HashSet<int> filterIds)
        {
            foreach (var filterId in filterIds)
            {
                var filter = _filtersCollection[filterId];

                var pass = _masks[id].InclusivePass(filter.Includes);
                pass &= _masks[id].ExclusivePass(filter.Excludes);

                if (!pass)
                    continue;
                //could try to remove same id several times due to delayed set modification operations
                filter.Remove(id);
            }
        }

        private void UpdateFiltersOnAdd<T>(int id)
        {
            var componentId = ComponentMeta<T>.Id;
            if (_excludeUpdateSets.ContainsKey(componentId))
                RemoveIdFromFilters(id, _excludeUpdateSets[componentId]);

            _masks[id].Set(componentId);

            if (!_includeUpdateSets.ContainsKey(componentId))
            {
#if UNITY
                _includeUpdateSets.Add(componentId, new HashSet<int>());
#else
                _includeUpdateSets.Add(componentId, new HashSet<int>(EcsCacheSettings.UpdateSetSize));
#endif
            }
            AddIdToFlters(id, _includeUpdateSets[componentId]);
        }

        private void UpdateFiltersOnRemove(int componentId, int id)
        {
            RemoveIdFromFilters(id, _includeUpdateSets[componentId]);
#if DEBUG
            if (_masks[id].Length <= componentId)
                throw new EcsException("there was no component ever");
#endif
            _masks[id].Unset(componentId);

            if (!_excludeUpdateSets.ContainsKey(componentId))
            {
#if UNITY
                _excludeUpdateSets.Add(componentId, new HashSet<int>());
#else
                _excludeUpdateSets.Add(componentId, new HashSet<int>(EcsCacheSettings.UpdateSetSize));
#endif
            }
            AddIdToFlters(id, _excludeUpdateSets[componentId]);
        }

        public ref T AddComponent<T>(int id, T component = default)
        {
            UpdateFiltersOnAdd<T>(id);

            var componentId = ComponentMeta<T>.Id;
            if (!_componentsPools.Contains(componentId))
                _componentsPools.Add(componentId, new ComponentsPool<T>(EcsCacheSettings.PoolSize));
            var pool = (ComponentsPool<T>)_componentsPools[componentId];
#if DEBUG
            if (pool == null)
                throw new EcsException("invalid pool");
#endif
            return ref pool.Add(id, component);
        }

        public void AddTag<T>(int id)
        {
            UpdateFiltersOnAdd<T>(id);

            var componentId = ComponentMeta<T>.Id;
            if (!_componentsPools.Contains(componentId))
                _componentsPools.Add(componentId, new TagsPool<T>(EcsCacheSettings.PoolSize));
            var pool = (TagsPool<T>)_componentsPools[componentId];
#if DEBUG
            if (pool == null)
                throw new EcsException("invalid pool");
#endif
            pool.Add(id);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(int id)
        {
#if DEBUG
            if (!Have<T>(id))
                throw new EcsException("entity have no " + typeof(T));
#endif
            var pool = (ComponentsPool<T>)_componentsPools[ComponentMeta<T>.Id];
            return ref pool._values[pool._sparse[id]];
        }

#if DEBUG
        private string DebugString(int id, int componentId) => _componentsPools[componentId].DebugString(id);

        public void DebugEntity(int id, StringBuilder sb)
        {
            var mask = _masks[id];
            var nextSetBit = mask.GetNextSetBit(0);
            while (nextSetBit != -1)
            {
                sb.Append("\n\t" + DebugString(id, nextSetBit));
                nextSetBit = mask.GetNextSetBit(nextSetBit + 1);
            }
        }

        public void DebugAll(StringBuilder sb)
        {
            for (int i = 0; i < _entites.Length; i++)
            {
                var entity = _entites[i];
                var id = entity.ToId();
                if (!IsDead(id))
                {
                    sb.Append(id + ":");
                    DebugEntity(id, sb);
                    sb.Append('\n');
                }
            }    
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetOrAddComponent<T>(int id)
        {
            if (Have<T>(id))
                return ref GetComponent<T>(id);
            else
                return ref AddComponent<T>(id);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveComponent<T>(int id) => RemoveComponent(id, ComponentMeta<T>.Id);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RemoveComponent(int id, int componentId)
        {
            UpdateFiltersOnRemove(componentId, id);
            _componentsPools[componentId].Remove(id);
        }
#endregion
#region Filters methods

        private void AddFilterToUpdateSets(in BitMask components, int filterIdx
            , Dictionary<int, HashSet<int>> sets)
        {
            var nextSetBit = components.GetNextSetBit(0);
            while (nextSetBit != -1)
            {
                if (!sets.ContainsKey(nextSetBit))
                {
#if UNITY
                    sets.Add(nextSetBit, new HashSet<int>());
#else
                    sets.Add(nextSetBit, new HashSet<int>(EcsCacheSettings.UpdateSetSize));
#endif
                }

#if DEBUG
                if (sets[nextSetBit].Contains(filterIdx))
                    throw new EcsException("set already contains this filter!");
#endif

                sets[nextSetBit].Add(filterIdx);

                nextSetBit = components.GetNextSetBit(nextSetBit + 1);
            }
        }

        public int RegisterFilter(in BitMask includes)
        {
            BitMask defaultExcludes = default;
            return RegisterFilter(in includes, in defaultExcludes);
        }

        public int RegisterFilter(in BitMask includes, in BitMask excludes)
        {
            int filterId;
            if (_filtersCollection.TryAdd(in includes, in excludes, out filterId))
            {
                var filter = _filtersCollection[filterId];
                AddFilterToUpdateSets(in filter.Includes, filterId, _includeUpdateSets);
                AddFilterToUpdateSets(in filter.Excludes, filterId, _excludeUpdateSets);
            }

            return filterId;
        }

        public EcsFilter GetFilter(int id) => _filtersCollection[id];
#endregion
    }
}
