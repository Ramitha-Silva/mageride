package lk.mageride.driver

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import lk.mageride.shared.data.models.RideState

/**
 * The whole driver shell: one activity, five throwaway screens (C025).
 *
 * It claims no SCR-DA id. C068–C070 own the real driver screens and Wave 4a replaces every
 * composable here; what this proves is that `:shared` composes into an app — the api-client signs
 * in and drives the ride, and the MQTT contract publishes what position-processor-svc reads.
 */
internal class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        setContent {
            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    DriverApp()
                }
            }
        }
    }
}

@Composable
private fun DriverApp(model: MainViewModel = viewModel()) {
    val state by model.state.collectAsStateWithLifecycle()

    Column(
        modifier = Modifier.fillMaxSize().padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("MageRide driver — walking skeleton", style = MaterialTheme.typography.titleMedium)

        if (state.busy) {
            LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
        }

        state.error?.let { message ->
            Text("error: $message", color = MaterialTheme.colorScheme.error)
        }

        when (state.screen) {
            Screen.SignIn -> SignIn(state, model)
            Screen.Otp -> Otp(state, model)
            Screen.Standby -> Standby(state, model)
            Screen.Offer -> Offer(state, model)
            Screen.OnRide -> OnRide(state, model)
        }
    }
}

@Composable
private fun SignIn(state: UiState, model: MainViewModel) {
    // Phone OTP only (AL-07). The seeded skeleton driver is +94770000001.
    OutlinedTextField(
        value = state.phone,
        onValueChange = model::onPhoneChanged,
        label = { Text("Phone") },
        modifier = Modifier.fillMaxWidth(),
    )
    Button(onClick = model::requestOtp, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
        Text("Send code")
    }
}

@Composable
private fun Otp(state: UiState, model: MainViewModel) {
    OutlinedTextField(
        value = state.otp,
        onValueChange = model::onOtpChanged,
        label = { Text("Code from the SMS") },
        modifier = Modifier.fillMaxWidth(),
    )
    Button(onClick = model::verifyOtp, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
        Text("Verify")
    }
}

@Composable
private fun Standby(state: UiState, model: MainViewModel) {
    var sessionJwt by remember { mutableStateOf("") }

    Text(if (state.online) "On standby — waiting for an offer" else "Offline")

    Button(onClick = model::goOnline, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
        Text("Go online")
    }

    // Typed in because `POST /v1/auth/mqtt-token` does not exist yet (C020 left it to C026), so
    // nothing can hand this app a device credential. `:shared`'s MqttSessionTokenManager is
    // already written against that endpoint and C076 uses it the day it lands.
    OutlinedTextField(
        value = sessionJwt,
        onValueChange = { sessionJwt = it },
        label = { Text("MQTT session JWT (C026 mints this)") },
        modifier = Modifier.fillMaxWidth(),
    )
    Button(
        onClick = { model.startPublishing(sessionJwt) },
        enabled = !state.busy && !state.publishing && sessionJwt.isNotBlank(),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Text(if (state.publishing) "Publishing position" else "Start publishing position")
    }
}

@Composable
private fun Offer(state: UiState, model: MainViewModel) {
    // The countdown renders from ride-svc's `offerExpiresAt`, not from a local timer started when
    // the offer appeared: it is ride-svc's `offer_expires_at > now()` that decides the accept, and
    // a second clock would let the screen disagree with the server about the boundary (§11.11).
    Text("Incoming ride", style = MaterialTheme.typography.titleLarge)
    Text("${state.secondsLeft ?: 0}s left", style = MaterialTheme.typography.headlineMedium)

    // See MainViewModel's KDoc: the offer id has no REST source, and the push that carries it is
    // C051/C041's.
    OutlinedTextField(
        value = state.offerId,
        onValueChange = model::onOfferIdChanged,
        label = { Text("offerId (the offer.created push carries this)") },
        modifier = Modifier.fillMaxWidth(),
    )
    Button(onClick = model::accept, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
        Text("Accept")
    }
}

@Composable
private fun OnRide(state: UiState, model: MainViewModel) {
    Text("Ride ${state.rideId}", style = MaterialTheme.typography.bodySmall)
    Text("State: ${state.rideState}", style = MaterialTheme.typography.titleLarge)

    // One button per transition, enabled only where the ride actually is. The authoritative guard
    // is ride-svc's (R-01); this is the cheap local one C015's RideTransitions exists to make
    // possible, kept crude here on purpose.
    Button(
        onClick = model::arrive,
        enabled = !state.busy && state.rideState == RideState.Accepted,
        modifier = Modifier.fillMaxWidth(),
    ) { Text("I have arrived") }

    Button(
        onClick = model::start,
        enabled = !state.busy && state.rideState == RideState.DriverArrived,
        modifier = Modifier.fillMaxWidth(),
    ) { Text("Start ride") }

    Button(
        onClick = model::complete,
        enabled = !state.busy && state.rideState == RideState.InProgress,
        modifier = Modifier.fillMaxWidth(),
    ) { Text("Complete ride") }
}
