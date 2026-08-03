package lk.mageride.shared.data.models

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import lk.mageride.shared.data.models.comms.AcknowledgeNotificationRequest
import lk.mageride.shared.data.models.comms.CallCounterparty
import lk.mageride.shared.data.models.comms.CallOutcome
import lk.mageride.shared.data.models.comms.CalleeRole
import lk.mageride.shared.data.models.comms.IssueVoipTokenRequest
import lk.mageride.shared.data.models.comms.NotificationPreferences
import lk.mageride.shared.data.models.comms.NotificationPriority
import lk.mageride.shared.data.models.comms.RecordCallOutcomeRequest
import lk.mageride.shared.data.models.comms.RegisterPushTokenRequest
import lk.mageride.shared.data.models.comms.SendNotificationRequest
import lk.mageride.shared.data.models.comms.SendNotificationResponse
import lk.mageride.shared.data.models.comms.StartCallRequest
import lk.mageride.shared.data.models.comms.StartCallResponse
import lk.mageride.shared.data.models.comms.VoipSession
import lk.mageride.shared.data.models.comms.VoipTokenResponse
import lk.mageride.shared.data.models.content.AuthoredFaqArticle
import lk.mageride.shared.data.models.content.AuthoredFaqListResponse
import lk.mageride.shared.data.models.content.Broadcast
import lk.mageride.shared.data.models.content.BroadcastListResponse
import lk.mageride.shared.data.models.content.NotificationTemplate
import lk.mageride.shared.data.models.content.NotificationTemplateVersion
import lk.mageride.shared.data.models.content.OnboardingSlide
import lk.mageride.shared.data.models.content.OnboardingSlidesResponse
import lk.mageride.shared.data.models.content.OperatingCity
import lk.mageride.shared.data.models.content.OperatingCityListResponse
import lk.mageride.shared.data.models.content.TemplateVersionStatus
import lk.mageride.shared.data.models.content.TrilingualText
import lk.mageride.shared.data.models.content.UpdateNotificationTemplateRequest
import lk.mageride.shared.data.models.query.EarningsPeriod
import lk.mageride.shared.data.models.query.EarningsSummary
import lk.mageride.shared.data.models.query.GeocodedPlace
import lk.mageride.shared.data.models.query.GeocodedPlaceSource
import lk.mageride.shared.data.models.query.GeometrySource
import lk.mageride.shared.data.models.query.NearbyVehicle
import lk.mageride.shared.data.models.query.NearbyVehiclesResponse
import lk.mageride.shared.data.models.query.PlaceSearchResponse
import lk.mageride.shared.data.models.query.SessionEarning
import lk.mageride.shared.data.models.query.TransportOption
import lk.mageride.shared.data.models.query.TransportOptionKind
import lk.mageride.shared.data.models.query.TransportOptionsResponse
import lk.mageride.shared.data.models.query.TripDetail
import lk.mageride.shared.data.models.query.TripDriver
import lk.mageride.shared.data.models.query.TripPlane
import lk.mageride.shared.data.models.query.TripSummary
import lk.mageride.shared.data.models.safety.BlockDriverRequest
import lk.mageride.shared.data.models.safety.ReportVehicleRequest
import lk.mageride.shared.data.models.safety.SharedTripVehicle
import lk.mageride.shared.data.models.safety.SharedTripView
import lk.mageride.shared.data.models.safety.SosDispatched
import lk.mageride.shared.data.models.safety.SosEvent
import lk.mageride.shared.data.models.safety.SosRole
import lk.mageride.shared.data.models.safety.SosSmsStatus
import lk.mageride.shared.data.models.safety.SosSource
import lk.mageride.shared.data.models.safety.TriggerSosRequest
import lk.mageride.shared.data.models.safety.TripShareLink
import lk.mageride.shared.data.models.safety.VehicleReport
import lk.mageride.shared.data.models.safety.VehicleReportStatus
import lk.mageride.shared.data.models.support.CreateSupportTicketRequest
import lk.mageride.shared.data.models.support.FaqArticle
import lk.mageride.shared.data.models.support.FaqListResponse
import lk.mageride.shared.data.models.support.FaqSummary
import lk.mageride.shared.data.models.support.Ticket
import lk.mageride.shared.data.models.support.TicketDetail
import lk.mageride.shared.data.models.support.TicketEvent
import lk.mageride.shared.data.models.support.TicketEventKind
import lk.mageride.shared.data.models.support.TicketQueue
import lk.mageride.shared.data.models.support.TicketRef
import lk.mageride.shared.data.models.support.TicketStatus
import lk.mageride.shared.data.models.support.UploadedScreenshot
import lk.mageride.shared.data.models.transit.FeedIssue
import lk.mageride.shared.data.models.transit.FeedStatus
import lk.mageride.shared.data.models.transit.FeedUploadStatus
import lk.mageride.shared.data.models.transit.FeedVersion
import lk.mageride.shared.data.models.transit.GtfsUploadAccepted
import lk.mageride.shared.data.models.transit.GtfsValidationReport
import lk.mageride.shared.data.models.transit.ImportGtfsFeedRequest
import lk.mageride.shared.data.models.transit.ImportGtfsFeedResponse
import lk.mageride.shared.data.models.transit.ParsedMapsLink
import lk.mageride.shared.data.models.transit.TransitCoverage
import lk.mageride.shared.data.models.transit.TransitLeg
import lk.mageride.shared.data.models.transit.TransitOption
import lk.mageride.shared.data.models.transit.TransitOptionKind
import lk.mageride.shared.data.models.transit.TransitOptionsResponse
import lk.mageride.shared.data.models.transit.TransitRoute
import lk.mageride.shared.data.models.transit.TransitStop
import lk.mageride.shared.data.models.version.AppVersionCheck
import kotlin.test.Test

/**
 * Round-trips every query-svc, transit-svc, safety-svc, support-svc, content-svc, voip-svc,
 * notification-svc and version-check DTO.
 *
 * See [assertRoundTrips].
 */
class DtoRoundTripReadTest {

    // ---- query.yaml --------------------------------------------------------------------------

    @Test
    fun the_live_map_dtos_round_trip() {
        val vehicle = NearbyVehicle(
            vehicleId = Sample.ULID_A,
            type = VehicleType.BUS,
            mode = ServiceMode.A,
            lat = 6.9271,
            lng = 79.8612,
            heading = 270,
            speed = 11.8,
            driverName = "Nimal",
            etaSeconds = 180,
            registrationNumber = "NC-1234",
        )
        assertRoundTrips(vehicle)
        assertRoundTrips(NearbyVehiclesResponse(listOf(vehicle), Sample.AT))
        val option = TransportOption(
            kind = TransportOptionKind.PUBLIC,
            label = "Route 138",
            vehicleType = VehicleType.BUS,
            routeNumber = "138",
            etaSeconds = 600,
            estimatedFareMinor = 5_000,
            currency = Currency.LKR,
            transfers = 0,
        )
        assertRoundTrips(option)
        assertRoundTrips(TransportOptionsResponse(listOf(option)))
    }

    @Test
    fun the_trip_history_and_earnings_dtos_round_trip() {
        assertRoundTrips(
            TripSummary(
                tripId = Sample.ULID_A,
                plane = TripPlane.SESSION,
                mode = ServiceMode.B,
                pickup = Sample.PLACE,
                dropoff = Sample.PLACE,
                fareMinor = 45_000,
                currency = Currency.LKR,
                startedAt = Sample.AT,
                endedAt = Sample.LATER,
            ),
        )
        assertRoundTrips(TripDriver(Sample.ULID_B, "Nimal", "WP-CAB-1234"))
        assertRoundTrips(
            TripDetail(
                tripId = Sample.ULID_A,
                plane = TripPlane.RIDE,
                mode = ServiceMode.C,
                pickup = Sample.PLACE,
                dropoff = Sample.PLACE,
                fareMinor = 45_000,
                currency = Currency.LKR,
                startedAt = Sample.AT,
                endedAt = Sample.LATER,
                polyline = "_p~iF~ps|U_ulLnnqC",
                distanceKm = 5.4,
                durationSec = 900,
                driver = TripDriver(Sample.ULID_B, "Nimal", "WP-CAB-1234"),
                rating = 5,
                geometrySource = GeometrySource.TELEMETRY,
            ),
        )
    }

    @Test
    fun the_earnings_dtos_round_trip() {
        assertRoundTrips(
            EarningsSummary(
                period = EarningsPeriod.WEEK,
                rangeFrom = Sample.DAY,
                rangeTo = Sample.DAY,
                grossMinor = 420_000,
                dailyFeeMinor = 70_000,
                penaltyMinor = 5_000,
                tipMinor = 12_000,
                netMinor = 357_000,
                currency = Currency.LKR,
                trips = 34,
            ),
        )
        assertRoundTrips(
            SessionEarning(
                tripId = Sample.ULID_A,
                grossMinor = 45_000,
                dailyFeeMinor = 10_000,
                penaltyMinor = 0,
                tipMinor = 5_000,
                netMinor = 40_000,
                currency = Currency.LKR,
                endedAt = Sample.LATER,
            ),
        )
    }

    @Test
    fun the_geocoding_dtos_round_trip() {
        val place = GeocodedPlace(
            lat = 6.9271,
            lng = 79.8612,
            displayName = "Colombo Fort Railway Station",
            line1 = "Olcott Mawatha",
            city = "Colombo",
            source = GeocodedPlaceSource.NOMINATIM,
        )
        assertRoundTrips(place)
        assertRoundTrips(PlaceSearchResponse(listOf(place)))
    }

    // ---- transit.yaml ------------------------------------------------------------------------

    @Test
    fun the_transit_routing_dtos_round_trip() {
        val leg = TransitLeg(
            routeId = "R138",
            routeShortName = "138",
            headsign = "Colombo Fort",
            description = "Kottawa – Colombo Fort",
            boardStopId = "S1",
            alightStopId = "S9",
            shape = "_p~iF~ps|U",
        )
        assertRoundTrips(leg)
        val option = TransitOption(
            kind = TransitOptionKind.TRANSIT,
            totalDurationSec = 2_700,
            walkingDistanceM = 450,
            legs = listOf(leg),
        )
        assertRoundTrips(option)
        assertRoundTrips(TransitOptionsResponse(listOf(option), "2026-07-01", TransitCoverage.ACTIVE))
        val stop = TransitStop("S1", "Kottawa", 6.8410, 79.9650, sequence = 1, distanceM = 120)
        assertRoundTrips(stop)
        assertRoundTrips(
            TransitRoute(
                routeId = "R138",
                routeShortName = "138",
                routeLongName = "Kottawa – Colombo Fort",
                agencyName = "SLTB",
                shape = "_p~iF~ps|U",
                stops = listOf(stop),
                nearestStops = listOf(stop),
            ),
        )
        assertRoundTrips(ParsedMapsLink(6.9271, 79.8612, "Colombo Fort"))
    }

    @Test
    fun the_gtfs_dataset_manager_dtos_round_trip() {
        assertRoundTrips(GtfsUploadAccepted(Sample.ULID_A))
        val issue = FeedIssue("stop_times.txt", row = 4_182, code = "unknown_stop_id", message = "S9999")
        assertRoundTrips(issue)
        assertRoundTrips(GtfsValidationReport(errors = listOf(issue), warnings = listOf(issue)))
        val counts = mapOf("agency" to 12L, "routes" to 431L, "stop_times" to 512_303L)
        assertRoundTrips(
            FeedUploadStatus(
                feedVersionId = Sample.ULID_A,
                status = FeedStatus.VALIDATING,
                counts = counts,
                feedInfoVersion = "2026-07-01",
                serviceStart = Sample.DAY,
                serviceEnd = Sample.MONTH,
                warnings = listOf("stable-id changed for 12 stops"),
                errorSummary = listOf("unknown_stop_id x 3"),
            ),
        )
        assertRoundTrips(
            FeedVersion(
                feedVersionId = Sample.ULID_A,
                feedInfoVersion = "2026-07-01",
                fileName = "sltb-2026-07.zip",
                sha256 = "0".repeat(64),
                uploadedBy = Sample.ULID_B,
                uploadedAt = Sample.AT,
                counts = counts,
                status = FeedStatus.ARCHIVED,
                activatedAt = Sample.AT,
                archivedAt = Sample.LATER,
            ),
        )
        assertRoundTrips(ImportGtfsFeedRequest(Sample.ULID_A))
        assertRoundTrips(ImportGtfsFeedResponse(Sample.ULID_A, FeedStatus.UPLOADED))
    }

    // ---- safety.yaml -------------------------------------------------------------------------

    @Test
    fun the_safety_dtos_round_trip() {
        assertRoundTrips(TriggerSosRequest(Sample.ULID_A, 6.9271, 79.8612, SosRole.DRIVER))
        assertRoundTrips(SosDispatched(Sample.ULID_A, Sample.AT, SosSmsStatus.DISPATCHED))
        assertRoundTrips(
            SosEvent(
                sosId = Sample.ULID_A,
                rideId = Sample.ULID_B,
                role = SosRole.PASSENGER,
                lat = 6.9271,
                lng = 79.8612,
                source = SosSource.WEB,
                acknowledgedAt = Sample.LATER,
                dispatchedAt = Sample.AT,
            ),
        )
        assertRoundTrips(TripShareLink("tok_abc", "https://mageride.lk/t/tok_abc", Sample.LATER))
        assertRoundTrips(SharedTripVehicle(VehicleType.SEDAN, "WP-CAB-1234"))
        assertRoundTrips(
            SharedTripView(
                state = "InProgress",
                position = Sample.POINT,
                heading = 270,
                vehicle = SharedTripVehicle(VehicleType.SEDAN, "WP-CAB-1234"),
                driverName = "Nimal",
                etaSeconds = 420,
                asOf = Sample.AT,
                expiresAt = Sample.LATER,
            ),
        )
        assertRoundTrips(ReportVehicleRequest(Sample.ULID_A, "Reckless driving", Sample.ULID_B))
        assertRoundTrips(
            VehicleReport(
                reportId = Sample.ULID_A,
                vehicleId = Sample.ULID_B,
                reason = "Reckless driving",
                tripId = Sample.ULID_C,
                status = VehicleReportStatus.CONFIRMED,
                createdAt = Sample.AT,
            ),
        )
        assertRoundTrips(BlockDriverRequest("Made me uncomfortable"))
    }

    // ---- support.yaml ------------------------------------------------------------------------

    @Test
    fun the_support_dtos_round_trip() {
        assertRoundTrips(TicketRef(Sample.ULID_A))
        val summary = FaqSummary(Sample.ULID_A, "How do I top up?", "wallet", Language.TA)
        assertRoundTrips(summary)
        assertRoundTrips(FaqListResponse(listOf(summary)))
        assertRoundTrips(
            FaqArticle(
                articleId = summary.articleId,
                title = summary.title,
                category = summary.category,
                language = summary.language,
                body = "# Topping up\n\nOpen Wallet…",
            ),
        )
        assertRoundTrips(
            CreateSupportTicketRequest("daily_fee_refund", "Charged twice", Sample.ULID_B, Sample.ULID_C),
        )
        assertRoundTrips(
            Ticket(
                ticketId = Sample.ULID_A,
                category = "daily_fee_refund",
                status = TicketStatus.RESOLVED,
                queue = TicketQueue.FINANCE,
                tripId = Sample.ULID_B,
                createdAt = Sample.AT,
                updatedAt = Sample.LATER,
                resolvedAt = Sample.LATER,
            ),
        )
        assertRoundTrips(
            TicketDetail(
                ticketId = Sample.ULID_A,
                category = "daily_fee_refund",
                status = TicketStatus.IN_PROGRESS,
                queue = TicketQueue.FINANCE,
                tripId = Sample.ULID_B,
                createdAt = Sample.AT,
                updatedAt = Sample.LATER,
                resolvedAt = Sample.LATER,
                description = "Charged twice on the same day",
                screenshotUrl = Sample.URL,
                adminResponse = "Reviewing",
                thread = listOf(
                    TicketEvent(
                        kind = TicketEventKind.RESPONDED,
                        at = Sample.LATER,
                        fromStatus = TicketStatus.OPEN,
                        toStatus = TicketStatus.IN_PROGRESS,
                        body = "Reviewing",
                        actorRole = "support_agent",
                    ),
                ),
            ),
        )
    }

    // ---- content.yaml ------------------------------------------------------------------------

    @Test
    fun the_content_dtos_round_trip() {
        val city = OperatingCity(
            code = "kandy",
            nameEn = "Kandy",
            nameSi = "Maha Nuwara",
            nameTa = "Kandi",
            centroid = GeoPoint(7.2906, 80.6337),
            sortOrder = 1,
        )
        assertRoundTrips(city)
        assertRoundTrips(OperatingCityListResponse(listOf(city)))
        val text = TrilingualText(si = "s", ta = "t", en = "e")
        assertRoundTrips(text)
        assertRoundTrips(
            NotificationTemplate("ride_offer", Language.EN, 3, "New ride", "Pickup {{pickup}}", listOf("pickup")),
        )
        assertRoundTrips(UpdateNotificationTemplateRequest(text, text))

        // Δ MCS-03 — the authored FAQ rows and AL-28's carousel.
        val article = AuthoredFaqArticle(Sample.ULID_A, "wallet", "Topping up", "Open Wallet…", sortOrder = 1)
        assertRoundTrips(article)
        assertRoundTrips(AuthoredFaqListResponse(Language.EN, listOf(article)))
        val slide = OnboardingSlide(slot = 1, illustrationRef = "onboarding/driver-wallet", title = text, body = text)
        assertRoundTrips(slide)
        assertRoundTrips(OnboardingSlidesResponse(listOf(slide)))
        assertRoundTrips(
            UploadedScreenshot(Sample.ULID_A, sizeBytes = 82_140, sha256 = "a".repeat(64), autoDeleteAt = Sample.LATER),
        )
        assertRoundTrips(NotificationTemplateVersion("ride_offer", 4, TemplateVersionStatus.DRAFT))
        val broadcast = Broadcast(Sample.ULID_A, "Service update", Sample.AT, Sample.LATER)
        assertRoundTrips(broadcast)
        assertRoundTrips(BroadcastListResponse(listOf(broadcast)))
    }

    // ---- voip.yaml and notification.yaml -----------------------------------------------------

    @Test
    fun the_comms_dtos_round_trip() {
        assertRoundTrips(IssueVoipTokenRequest(Sample.ULID_A))
        val session = VoipSession("ride_${Sample.ULID_A}", "lk_jwt", "wss://voip.mageride.lk")
        assertRoundTrips(session)
        assertRoundTrips(
            VoipTokenResponse(
                roomName = session.roomName,
                token = session.token,
                wsUrl = session.wsUrl,
                callee = CallCounterparty.RIDER,
            ),
        )
        assertRoundTrips(StartCallRequest(Sample.ULID_A, CalleeRole.SENDER, CallType.FREE_VOIP))
        assertRoundTrips(StartCallResponse(Sample.ULID_A, CallType.FREE_VOIP, session))
        assertRoundTrips(RegisterPushTokenRequest("fcm-token", ClientPlatform.IOS, "device-1"))
        assertRoundTrips(NotificationPreferences(mapOf("SCHEDULED_REMINDER" to true)))

        // Δ MCS-03 — E-01's acknowledgement and AL-48's call outcome.
        assertRoundTrips(AcknowledgeNotificationRequest(Sample.ULID_A))
        assertRoundTrips(RecordCallOutcomeRequest(CallOutcome.VOIP_FAILED))
        assertRoundTrips(
            SendNotificationRequest(
                notificationType = "RIDE_OFFER",
                templateKey = "ride_offer",
                recipients = listOf(Sample.ULID_A, Sample.ULID_B),
                data = JsonObject(mapOf("pickup" to JsonPrimitive("Colombo Fort"))),
            ),
        )
        assertRoundTrips(SendNotificationResponse(Sample.ULID_A, accepted = 2, suppressed = 1, undeliverable = 0))
    }

    // ---- version-check.yaml ------------------------------------------------------------------

    @Test
    fun the_version_gate_dto_round_trips() {
        assertRoundTrips(AppVersionCheck(true, "1.6.2", Sample.URL, isMandatory = true))
    }
}
