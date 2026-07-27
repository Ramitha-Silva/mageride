package lk.mageride.shared.data.models.version

import kotlinx.serialization.Serializable

// version-check — the minimum-app-version gate (D-31).
// Source: backend/contracts/version-check.yaml (D3' "version-check — gateway gate").
//
// The endpoint below is the app's "am I current?" poll at cold start. The ENFORCING half of D-31
// is gateway middleware (C008) that reads X-App-Version + X-Platform on EVERY request and answers
// 426 upgrade-required when the client is below the floor — that is not an endpoint, and its body
// is a ProblemDetails whose extensions carry updateUrl, latestVersion and isMandatory.

/**
 * `GET /v1/version/check` — 200 (US-17.1/17.2).
 *
 * Public: the answer must be obtainable by a client too old to authenticate.
 *
 * The same three extension fields appear on the gateway's `426` problem body, so a client has one
 * place to render the update prompt from whichever path it arrives on — see
 * `ProblemDetails.updateUrl` / `latestVersion` / `isMandatory`.
 *
 * @property updateRequired Whether a newer build exists.
 * @property latestVersion The newest published build, e.g. `1.6.2`.
 * @property updateUrl Play Store or App Store deep link for this platform.
 * @property isMandatory `true` when the client is below the **hard** floor and must update;
 *   `false` makes the prompt dismissible.
 */
@Serializable
public data class AppVersionCheck(
    val updateRequired: Boolean,
    val latestVersion: String,
    val updateUrl: String,
    val isMandatory: Boolean,
)
