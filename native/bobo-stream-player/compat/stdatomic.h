#pragma once

#if defined(_MSC_VER) && !defined(__clang__)

#include <stdbool.h>
#include <stdint.h>
#include <intrin.h>

typedef enum memory_order {
    memory_order_relaxed = 0,
    memory_order_consume = 1,
    memory_order_acquire = 2,
    memory_order_release = 3,
    memory_order_acq_rel = 4,
    memory_order_seq_cst = 5
} memory_order;

typedef volatile long atomic_bool;
typedef volatile long atomic_int;
typedef volatile unsigned long atomic_uint;

static __inline long _bobo_atomic_load_long(const volatile long* obj) {
    long v = *obj;
    _ReadWriteBarrier();
    return v;
}

static __inline void _bobo_atomic_store_long(volatile long* obj, long val) {
    _InterlockedExchange((volatile long*)obj, val);
}

static __inline long _bobo_atomic_exchange_long(volatile long* obj, long val) {
    return _InterlockedExchange((volatile long*)obj, val);
}

static __inline long _bobo_atomic_fetch_add_long(volatile long* obj, long arg) {
    return _InterlockedExchangeAdd((volatile long*)obj, arg);
}

static __inline long _bobo_atomic_fetch_sub_long(volatile long* obj, long arg) {
    return _InterlockedExchangeAdd((volatile long*)obj, -arg);
}

static __inline bool _bobo_atomic_cas_long(volatile long* obj, long* expected, long desired) {
    long exp = *expected;
    long prev = _InterlockedCompareExchange((volatile long*)obj, desired, exp);
    if (prev == exp) {
        return true;
    }
    *expected = prev;
    return false;
}

#define atomic_compare_exchange_strong_explicit(obj, expected, desired, succ, fail) \
    ((void)(succ), (void)(fail), _bobo_atomic_cas_long((volatile long*)(obj), (long*)(expected), (long)(desired)))

#define atomic_compare_exchange_weak_explicit(obj, expected, desired, succ, fail) \
    atomic_compare_exchange_strong_explicit((obj), (expected), (desired), (succ), (fail))

#define atomic_compare_exchange_strong(obj, expected, desired) \
    atomic_compare_exchange_strong_explicit((obj), (expected), (desired), memory_order_seq_cst, memory_order_seq_cst)

#define atomic_load_explicit(obj, order) \
    ((void)(order), _bobo_atomic_load_long((const volatile long*)(obj)))

#define atomic_store_explicit(obj, desired, order) \
    ((void)(order), _bobo_atomic_store_long((volatile long*)(obj), (long)(desired)))

#define atomic_exchange_explicit(obj, desired, order) \
    ((void)(order), _bobo_atomic_exchange_long((volatile long*)(obj), (long)(desired)))

#define atomic_fetch_add_explicit(obj, arg, order) \
    ((void)(order), _bobo_atomic_fetch_add_long((volatile long*)(obj), (long)(arg)))

#define atomic_fetch_sub_explicit(obj, arg, order) \
    ((void)(order), _bobo_atomic_fetch_sub_long((volatile long*)(obj), (long)(arg)))

#define atomic_load(obj) atomic_load_explicit((obj), memory_order_seq_cst)
#define atomic_store(obj, desired) atomic_store_explicit((obj), (desired), memory_order_seq_cst)
#define atomic_exchange(obj, desired) atomic_exchange_explicit((obj), (desired), memory_order_seq_cst)
#define atomic_fetch_add(obj, arg) atomic_fetch_add_explicit((obj), (arg), memory_order_seq_cst)
#define atomic_fetch_sub(obj, arg) atomic_fetch_sub_explicit((obj), (arg), memory_order_seq_cst)

#else
#include_next <stdatomic.h>
#endif
