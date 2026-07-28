package lk.mageride.passenger

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel

/**
 * The whole passenger shell: one activity, five throwaway screens (C025).
 *
 * It claims no SCR-PA id. C077–C080 own the real passenger screens and Wave 4a replaces every
 * composable here; what this proves is that `:shared` composes into an app at all — the api-client
 * signs in and books, the `LiveHub` contract delivers positions, and the geocell maths joins the
 * right 19 groups.
 */
internal class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        setContent {
            MaterialTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    PassengerApp()
                }
            }
        }
    }
}

@Composable
private fun PassengerApp(model: MainViewModel = viewModel()) {
    val state by model.state.collectAsStateWithLifecycle()

    Column(
        modifier = Modifier.fillMaxSize().padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("MageRide passenger — walking skeleton", style = MaterialTheme.typography.titleMedium)

        if (state.busy) {
            LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
        }

        state.error?.let { message ->
            // The kebab error code is what a real screen resolves Si/Ta/En copy from (D-26); a
            // skeleton shows the raw message because there is nothing to resolve it against yet.
            Text("error: $message", color = MaterialTheme.colorScheme.error)
        }

        when (state.screen) {
            Screen.SignIn -> SignIn(state, model)
            Screen.Otp -> Otp(state, model)
            Screen.Map -> LiveMap(state, model)
            Screen.Booking, Screen.InRide -> Ride(state, model)
        }
    }
}

@Composable
private fun SignIn(state: UiState, model: MainViewModel) {
    // Phone OTP is the only way into either app (AL-07). Google and Apple sign-in exist on the
    // portals and nowhere else.
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
private fun LiveMap(state: UiState, model: MainViewModel) {
    // A list, not a map. C077 owns MapLibre over PMTiles; what matters here is that the frames
    // arrive at all, over a real socket, from the 19 cells this client joined.
    Text("Joined ${state.cells.size} geocells (res-7 + ring 2)")
    Text("${state.vehicles.size} vehicles nearby", style = MaterialTheme.typography.bodySmall)

    LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxWidth()) {
        items(state.vehicles) { vehicle ->
            Card(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(12.dp)) {
                    Text(vehicle.type ?: "vehicle")
                    Text("${vehicle.lat}, ${vehicle.lng}", style = MaterialTheme.typography.bodySmall)
                }
            }
        }
    }

    Button(onClick = model::book, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
        Text("Book Colombo Fort -> Dehiwala")
    }
}

@Composable
private fun Ride(state: UiState, model: MainViewModel) {
    Text("Ride ${state.rideId}", style = MaterialTheme.typography.bodySmall)
    Text("State: ${state.rideState}", style = MaterialTheme.typography.titleLarge)

    // Manual, because `RideStateChanged` on the hub is C041's. `signalr-hub.md` §1 already names
    // this REST read as the fallback; a button makes the polling visible instead of hiding it.
    Button(onClick = model::refreshRide, enabled = !state.busy, modifier = Modifier.fillMaxWidth()) {
        Text("Refresh")
    }
}
