package com.tianshuqitan.headless.core

/** Called serially by the Android default-network callback; ignores late callbacks for old networks. */
class DefaultNetworkState<T> {
    data class Change(val invalidateConnection: Boolean = false, val becameUsable: Boolean = false)
    var current: T? = null
        private set
    var usable: Boolean = false
        private set

    fun available(network: T): Change {
        if (current == network) return Change()
        val hadNetwork = current != null
        current = network
        usable = false // Wait for capabilities, never assume onAvailable implies Internet access.
        return Change(invalidateConnection = hadNetwork)
    }

    fun capabilities(network: T, internetValidated: Boolean): Change {
        if (current != network || usable == internetValidated) return Change()
        val wasUsable = usable
        usable = internetValidated
        return Change(invalidateConnection = wasUsable, becameUsable = usable)
    }

    fun lost(network: T): Change {
        if (current != network) return Change()
        current = null
        usable = false
        return Change(invalidateConnection = true)
    }
}
