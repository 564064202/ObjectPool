﻿/*
 * Generic Object Pool Implementation
 *
 * Implemented by Ofir Makmal, 28/1/2013
 *
 * My Blog: Blogs.microsoft.co.il/blogs/OfirMakmal
 * Email:   Ofir.Makmal@gmail.com
 *
 */

using CodeProject.ObjectPool.Core;
using System;
using System.Collections.Generic;
using System.Threading;

namespace CodeProject.ObjectPool
{
    /// <summary>
    ///   Generic object pool.
    /// </summary>
    /// <typeparam name="T">
    ///   The type of the object that which will be managed by the pool. The pooled object have to be
    ///   a sub-class of PooledObject.
    /// </typeparam>
    public sealed class ObjectPool<T> : IObjectPool<T>, IObjectPoolHandle
        where T : PooledObject
    {
        /// <summary>
        ///   The concurrent queue containing pooled objects.
        /// </summary>
        private readonly Stack<T> _pooledObjects = new Stack<T>();

        /// <summary>
        ///   Indication flag that states whether Adjusting operating is in progress. The type is
        ///   Int, altought it looks like it should be bool - this was done for Interlocked CAS
        ///   operation (CompareExchange).
        /// </summary>
        private int _adjustPoolSizeIsInProgressCasFlag; // 0 state false

        private int _maximumPoolSize;
        private int _minimumPoolSize;

        #region Public Properties

        /// <summary>
        ///   Gets the Diagnostics class for the current Object Pool, whose goal is to record data
        ///   about how the pool operates. By default, however, an object pool records anything; you
        ///   have to enable it through the <see cref="ObjectPoolDiagnostics.Enabled"/> property.
        /// </summary>
        public ObjectPoolDiagnostics Diagnostics { get; set; }

        /// <summary>
        ///   Gets the Factory method that will be used for creating new objects.
        /// </summary>
        public Func<T> FactoryMethod { get; }

        /// <summary>
        ///   Gets or sets the maximum number of objects that could be available at the same time in
        ///   the pool.
        /// </summary>
        public int MaximumPoolSize
        {
            get
            {
                return _maximumPoolSize;
            }
            set
            {
                ObjectPoolConstants.ValidatePoolLimits(MinimumPoolSize, value);
                _maximumPoolSize = value;
                AdjustPoolSizeToBounds(AdjustMode.Maximum);
            }
        }

        /// <summary>
        ///   Gets or sets the minimum number of objects in the pool.
        /// </summary>
        public int MinimumPoolSize
        {
            get
            {
                return _minimumPoolSize;
            }
            set
            {
                ObjectPoolConstants.ValidatePoolLimits(value, MaximumPoolSize);
                _minimumPoolSize = value;
                AdjustPoolSizeToBounds(AdjustMode.Minimum);
            }
        }

        /// <summary>
        ///   Gets the count of the objects currently in the pool.
        /// </summary>
        public int ObjectsInPoolCount => _pooledObjects.Count;

        #endregion Public Properties

        #region C'tor and Initialization code

        /// <summary>
        ///   Initializes a new pool with default settings.
        /// </summary>
        public ObjectPool()
            : this(ObjectPoolConstants.DefaultPoolMinimumSize, ObjectPoolConstants.DefaultPoolMaximumSize, null)
        {
        }

        /// <summary>
        ///   Initializes a new pool with specified minimum pool size and maximum pool size.
        /// </summary>
        /// <param name="minimumPoolSize">The minimum pool size limit.</param>
        /// <param name="maximumPoolSize">The maximum pool size limit</param>
        /// <exception cref="ArgumentOutOfRangeException">
        ///   <paramref name="minimumPoolSize"/> is less than zero,
        ///   <paramref name="maximumPoolSize"/> is less than or equal to zero, or
        ///   <paramref name="minimumPoolSize"/> is greater than <paramref name="maximumPoolSize"/>.
        /// </exception>
        public ObjectPool(int minimumPoolSize, int maximumPoolSize)
            : this(minimumPoolSize, maximumPoolSize, null)
        {
        }

        /// <summary>
        ///   Initializes a new pool with specified factory method.
        /// </summary>
        /// <param name="factoryMethod">The factory method that will be used to create new objects.</param>
        public ObjectPool(Func<T> factoryMethod)
            : this(ObjectPoolConstants.DefaultPoolMinimumSize, ObjectPoolConstants.DefaultPoolMaximumSize, factoryMethod)
        {
        }

        /// <summary>
        ///   Initializes a new pool with specified factory method and minimum and maximum size.
        /// </summary>
        /// <param name="minimumPoolSize">The minimum pool size limit.</param>
        /// <param name="maximumPoolSize">The maximum pool size limit</param>
        /// <param name="factoryMethod">The factory method that will be used to create new objects.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        ///   <paramref name="minimumPoolSize"/> is less than zero,
        ///   <paramref name="maximumPoolSize"/> is less than or equal to zero, or
        ///   <paramref name="minimumPoolSize"/> is greater than <paramref name="maximumPoolSize"/>.
        /// </exception>
        public ObjectPool(int minimumPoolSize, int maximumPoolSize, Func<T> factoryMethod)
        {
            // Validating pool limits, exception is thrown if invalid
            ObjectPoolConstants.ValidatePoolLimits(minimumPoolSize, maximumPoolSize);

            // Assigning properties
            FactoryMethod = factoryMethod;
            _maximumPoolSize = maximumPoolSize;
            _minimumPoolSize = minimumPoolSize;

            // Creating a new instance for the Diagnostics class
            Diagnostics = new ObjectPoolDiagnostics();

            // Initilizing objects in pool
            AdjustPoolSizeToBounds(AdjustMode.Minimum | AdjustMode.Maximum);
        }

        #endregion C'tor and Initialization code

        #region Finalizer

        /// <summary>
        ///   ObjectPool destructor.
        /// </summary>
        ~ObjectPool()
        {
            // The pool is going down, releasing the resources for all objects in pool.
            lock (_pooledObjects)
            {
                foreach (var item in _pooledObjects)
                {
                    DestroyPooledObject(item);
                }
            }
        }

        #endregion Finalizer

        #region Pool Operations

        /// <summary>
        ///   Clears the pool and destroys each object stored inside it.
        /// </summary>
        public void Clear()
        {
            // If there is an Adjusting/Clear operation in progress, wait until it is done.
            while (Interlocked.CompareExchange(ref _adjustPoolSizeIsInProgressCasFlag, 1, 0) != 0)
            {
                // Wait...
            }

            lock (_pooledObjects)
            {
                // Destroy all objects.
                while (_pooledObjects.Count > 0)
                {
                    var dequeuedObjectToDestroy = _pooledObjects.Pop();
                    DestroyPooledObject(dequeuedObjectToDestroy);
                }
            }

            // Finished clearing, allowing additional callers to enter when needed.
            _adjustPoolSizeIsInProgressCasFlag = 0;
        }

        /// <summary>
        ///   Gets a monitored object from the pool.
        /// </summary>
        /// <returns>A monitored object from the pool.</returns>
        public T GetObject()
        {
            // Just a quick check in order to avoid a useless lock.
            if (_pooledObjects.Count > 0)
            {
                lock (_pooledObjects)
                {
                    // Check again, collection might have changed.
                    if (_pooledObjects.Count > 0)
                    {
                        if (Diagnostics.Enabled)
                        {
                            Diagnostics.IncrementPoolObjectHitCount();
                        }
                        return _pooledObjects.Pop();
                    }
                }
            }

            // This should not happen normally, but could be happening when there is stress on the
            // pool. No available objects in pool, create a new one and return it to the caller.
            if (Diagnostics.Enabled)
            {
                Diagnostics.IncrementPoolObjectMissCount();
            }
            return CreatePooledObject();
        }

        void IObjectPoolHandle.ReturnObjectToPool(PooledObject objectToReturnToPool, bool reRegisterForFinalization)
        {
            var returnedObject = objectToReturnToPool as T;

            if (reRegisterForFinalization && Diagnostics.Enabled)
            {
                Diagnostics.IncrementObjectResurrectionCount();
            }

            // Checking that the pool is not full.
            if (_pooledObjects.Count < MaximumPoolSize)
            {
                // Reset the object state (if implemented) before returning it to the pool. If
                // reseting the object have failed, destroy the object.
                if (returnedObject != null && !returnedObject.ResetState())
                {
                    if (Diagnostics.Enabled)
                    {
                        Diagnostics.IncrementResetStateFailedCount();
                    }
                    DestroyPooledObject(returnedObject);
                    return;
                }

                // Re-registering for finalization - in case of resurrection (called from Finalize method).
                if (reRegisterForFinalization)
                {
                    GC.ReRegisterForFinalize(returnedObject);
                }

                // Adding the object back to the pool.
                if (Diagnostics.Enabled)
                {
                    Diagnostics.IncrementReturnedToPoolCount();
                }
                lock (_pooledObjects)
                {
                    _pooledObjects.Push(returnedObject);
                }
            }
            else
            {
                if (Diagnostics.Enabled)
                {
                    Diagnostics.IncrementPoolOverflowCount();
                }

                // The Pool's upper limit has exceeded, there is no need to add this object back into
                // the pool and we can destroy it.
                DestroyPooledObject(returnedObject);

                // We also make sure that the pool is not overflowing.
                AdjustPoolSizeToBounds(AdjustMode.Maximum);
            }
        }

        #endregion Pool Operations

        #region Private Methods

        internal void AdjustPoolSizeToBounds(AdjustMode adjustMode)
        {
            // If there is an Adjusting/Clear operation in progress, skip and return.
            if (Interlocked.CompareExchange(ref _adjustPoolSizeIsInProgressCasFlag, 1, 0) != 0)
            {
                return;
            }

            // If we reached this point, we've set the AdjustPoolSizeIsInProgressCASFlag to 1 (true)
            // using the above CAS function. We can now safely adjust the pool size without
            // interferences :)

            // Adjusting lower bound.
            if (adjustMode.HasFlag(AdjustMode.Minimum) && _pooledObjects.Count < MinimumPoolSize)
            {
                lock (_pooledObjects)
                {
                    while (_pooledObjects.Count < MinimumPoolSize)
                    {
                        _pooledObjects.Push(CreatePooledObject());
                    }
                }
                
            }

            // Adjusting upper bound.
            if (adjustMode.HasFlag(AdjustMode.Maximum) && _pooledObjects.Count > MaximumPoolSize)
            {
                lock (_pooledObjects)
                {
                    while (_pooledObjects.Count > MaximumPoolSize)
                    {
                        if (Diagnostics.Enabled)
                        {
                            Diagnostics.IncrementPoolOverflowCount();
                        }
                        var dequeuedObjectToDestroy = _pooledObjects.Pop();
                        DestroyPooledObject(dequeuedObjectToDestroy);
                    }
                }                
            }

            // Finished adjusting, allowing additional callers to enter when needed.
            _adjustPoolSizeIsInProgressCasFlag = 0;
        }

        private T CreatePooledObject()
        {
            if (Diagnostics.Enabled)
            {
                Diagnostics.IncrementObjectsCreatedCount();
            }

            // Throws an exception if the type doesn't have default ctor - on purpose! I've could've
            // add a generic constraint with new (), but I didn't want to limit the user and force a
            // parameterless c'tor.
            var newObject = FactoryMethod?.Invoke() ?? Activator.CreateInstance<T>();

            // Setting the 'return to pool' action in the newly created pooled object.
            newObject.Handle = this;
            return newObject;
        }

        private void DestroyPooledObject(PooledObject objectToDestroy)
        {
            // Making sure that the object is only disposed once (in case of application shutting
            // down and we don't control the order of the finalization).
            if (!objectToDestroy.Disposed)
            {
                if (Diagnostics.Enabled)
                {
                    Diagnostics.IncrementObjectsDestroyedCount();
                }

                // Deterministically release object resources, nevermind the result, we are
                // destroying the object.
                objectToDestroy.ReleaseResources();
                objectToDestroy.Disposed = true;
            }

            // The object is being destroyed, resources have been already released deterministically,
            // so we di no need the finalizer to fire.
            GC.SuppressFinalize(objectToDestroy);
        }

        #endregion Private Methods
    }
}