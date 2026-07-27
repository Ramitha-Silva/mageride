package lk.mageride.shared.di

import org.koin.core.KoinApplication
import org.koin.core.context.startKoin
import org.koin.core.module.Module

/**
 * Starts Koin with [sharedModules] plus whatever the app adds.
 *
 * Android calls this from `Application.onCreate` (and adds `androidContext(this)` in its own
 * declaration); iOS calls it from the `App` initialiser, which is why it exists at all —
 * Swift cannot express Koin's trailing-lambda DSL comfortably, so the shared layer owns the
 * start-up call and iOS passes only its extra modules.
 */
public fun initKoin(
    appModules: List<Module> = emptyList(),
    appDeclaration: KoinApplication.() -> Unit = {},
): KoinApplication = startKoin {
    appDeclaration()
    modules(sharedModules + appModules)
}
