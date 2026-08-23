package lk.mageride.driver.documents

import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import lk.mageride.driver.R
import lk.mageride.driver.ui.component.NoticeCard
import lk.mageride.driver.ui.component.SectionLabel
import lk.mageride.driver.ui.theme.MageRideTheme
import lk.mageride.shared.data.models.DocumentKind
import lk.mageride.shared.db.driver.CachedDocumentImage
import org.koin.androidx.compose.koinViewModel

/**
 * **SCR-DA-029a · the driver's own documents (Δ MCS-28).**
 *
 * The one standing placeholder `DriverNavHost` had left, and `mageride://documents` has always
 * pointed here (E-03). Reached from SCR-DA-029's *My documents* row and from a vehicle's card on
 * SCR-DA-026, which are the two places a driver goes looking.
 *
 * **This screen exists to work with no connection.** A driver is asked for a licence at a
 * checkpoint, a depot gate or the side of a road, and that is where a screen that needs signal is
 * worth nothing. The images come off disk (§3.17) and the refresh happens behind them; when it
 * fails, the documents stay and a note says the copies are local.
 *
 * The §0.4 fences are the store's, not this screen's, with one exception that belongs here: there
 * is **no share action and no download**. A driver who needs to send a licence somewhere has the
 * original in their own gallery.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun DocumentsScreen(onBack: () -> Unit, modifier: Modifier = Modifier) {
    val viewModel: DocumentsViewModel = koinViewModel()
    val state by viewModel.state.collectAsStateWithLifecycle()

    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = { Text(text = stringResource(R.string.documents_title)) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(imageVector = Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = null)
                    }
                },
            )
        },
    ) { insets ->
        LazyColumn(
            modifier = Modifier.padding(insets).fillMaxSize(),
            contentPadding = PaddingValues(MageRideTheme.spacing.md),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.sm),
        ) {
            if (state.offline) {
                item {
                    // A note rather than an error: the documents below are still the right ones to
                    // show, and this screen's whole purpose is the case where there is no network.
                    NoticeCard(accent = MaterialTheme.colorScheme.secondary) {
                        Text(text = stringResource(R.string.documents_offline))
                    }
                }
            }

            if (state.documents.isEmpty() && !state.loading) {
                item { Text(text = stringResource(R.string.documents_empty)) }
            }

            items(state.documents, key = CachedDocumentImage::documentId) { document ->
                DocumentCard(document = document)
            }
        }
    }
}

/** One document — its kind, whether the copy has aged, and the image itself. */
@Composable
private fun DocumentCard(document: CachedDocumentImage, modifier: Modifier = Modifier) {
    Surface(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(MageRideTheme.radius.card),
        color = MaterialTheme.colorScheme.surface,
    ) {
        Column(
            modifier = Modifier.padding(MageRideTheme.spacing.sm),
            verticalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs),
        ) {
            Row(horizontalArrangement = Arrangement.spacedBy(MageRideTheme.spacing.xs)) {
                SectionLabel(text = stringResource(documentKindLabel(document.kind)))
            }

            if (document.isStale) {
                // Shown rather than hidden: yesterday's certificate beats nothing at a checkpoint,
                // and the driver is told which it is. See `DocumentImageCache`.
                Text(
                    text = stringResource(R.string.documents_stale),
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            // Decoded once per distinct image rather than per recomposition — these are handset
            // photographs and a re-decode on every frame would be visible.
            val image = remember(document.bytes) { document.bytes.toImageBitmap() }

            if (image != null) {
                Image(
                    bitmap = image,
                    contentDescription = null,
                    modifier = Modifier.fillMaxWidth(),
                    // Fit, not crop: a document is read, and cropping one to a tidy rectangle is
                    // how the expiry date ends up outside the frame.
                    contentScale = ContentScale.Fit,
                )
            }
        }
    }
}

/** The five `registry.documents.kind` values, as copy. */
private fun documentKindLabel(kind: String): Int = when (kind) {
    DocumentKind.DRIVING_LICENSE.wire -> R.string.doc_kind_driving_license
    DocumentKind.REGISTRATION.wire -> R.string.doc_kind_registration
    DocumentKind.PERMIT.wire -> R.string.doc_kind_permit
    DocumentKind.INSURANCE.wire -> R.string.doc_kind_insurance
    else -> R.string.doc_kind_revenue_license
}

/**
 * A stored image as something Compose can draw, or `null` if it will not decode.
 *
 * Null rather than a throw, for the reason the avatar gives: the bytes came off this handset's own
 * disk, so a failure means a truncated write, and the answer to that is a card with no picture
 * rather than a crash on a screen a driver opened at a checkpoint.
 */
private fun ByteArray.toImageBitmap(): ImageBitmap? =
    runCatching { BitmapFactory.decodeByteArray(this, 0, size)?.asImageBitmap() }.getOrNull()
