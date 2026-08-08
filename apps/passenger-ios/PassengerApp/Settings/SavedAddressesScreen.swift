import MageRideShared
import SwiftUI

/// SCR-PI-026 — *"Saved addresses"*.
///
/// The cell, top to bottom: `‹ Settings · Saved addresses · ＋`, a `glist` holding the ★ **Home** and
/// ★ **Work** rows, the pin map, and the caption *"Drop / drag pin → reverse-geocoded (AL-14)"*.
///
/// **`＋` is in the navigation bar, and that is the Δ from Android.** `passenger_ios.html` puts it in
/// the `navtop`'s `.act` slot and `passenger_android.html` draws a full-width `＋ Add address` CTA at
/// the foot of the screen. Same action, same enabling rule (there has to be a pin), and the frames
/// disagree because a `List`-shaped iOS screen puts its create affordance in the bar.
///
/// **Home and Work are always drawn, set or not.** They are the two rows US-22.1 is about and the
/// wireframe prints them above everything else; a passenger who has saved neither still needs
/// somewhere to tap, and *"Not set"* under the row is that somewhere. Tapping either opens
/// SCR-PI-026a with the shortcut already decided — see ``SavedAddressesModel``, which is where the
/// reading of *"Home & Work via OSM pin"* is argued.
///
/// **The labelled rows are drawn under them, and the cell does not draw one.** The frame's `glist`
/// holds Home and Work only, because that is what its example account has; its own states line says
/// *"edit/delete"* and the Android screen lists them, so leaving them out would be a screen that
/// could create an address it could never show. Behaviour follows Android — the C099 split, applied.
@MainActor
struct SavedAddressesScreen: View {

    @StateObject private var model: SavedAddressesModel

    init(addresses: AddressBook, lastFix: LastKnownFix, keys: IdempotencyKeys) {
        _model = StateObject(
            wrappedValue: SavedAddressesModel(addresses: addresses, lastFix: lastFix, keys: keys)
        )
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                if let errorKey = model.state.errorKey {
                    FormErrorText(messageKey: errorKey)
                }

                if model.state.isLoading {
                    LoadingRow(messageKey: "addresses_loading")
                }

                GroupedList {
                    shortcutRow(.home, showsSeparator: true)
                    shortcutRow(.work, showsSeparator: !model.state.labelled.isEmpty)

                    // Keyed on the row's own id, and the last one asked for by identity rather than
                    // by index: **Swift has no key paths into tuples**, so
                    // `ForEach(Array(x.enumerated()), id: \.element.addressId)` does not compile —
                    // the finding `apps/driver-ios` records from C087 onwards and `ModeFilterSheet`
                    // and `PaymentMethodScreen` already follow.
                    ForEach(model.state.labelled, id: \.addressId) { address in
                        Button {
                            Task { await model.edit(address) }
                        } label: {
                            GroupedValueRow(
                                title: address.label,
                                subtitle: address.oneLine,
                                symbolName: "mappin.and.ellipse",
                                symbolTint: MageRideColor.outlineVariant,
                                showsSeparator: address.addressId != model.state.labelled.last?.addressId
                            ) {
                                RowChevron()
                            }
                        }
                        .buttonStyle(.plain)
                        .disabled(model.state.busyWith == address.addressId)
                    }
                }

                pinMap

                Text(key: "addresses_pin_hint")
                    .mageFont(.caption)
                    .foregroundStyle(MageRideColor.onSurfaceVariant)
            }
            .padding(MageRideSpacing.md)
        }
        .background(MageRideColor.surface)
        .navigationTitle(Text(key: "menu_saved_addresses"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .navigationBarTrailing) {
                Button {
                    Task { await model.addAddress() }
                } label: {
                    Image(systemName: "plus")
                }
                .disabled(!model.state.canAdd)
                .accessibilityLabel(Text(key: "addresses_add"))
            }
        }
        .refreshable { await model.refresh() }
        .task { await model.refresh() }
        .sheet(isPresented: sheetBinding) {
            if let sheet = model.state.sheet {
                AddAddressSheet(
                    sheet: sheet,
                    onLabelChanged: model.onLabelChanged,
                    onLine1Changed: model.onLine1Changed,
                    onLine2Changed: model.onLine2Changed,
                    onLine3Changed: model.onLine3Changed,
                    onSave: { Task { await model.save() } },
                    onDelete: { Task { await model.delete() } }
                )
            }
        }
    }

    // MARK: -

    /// The ★ Home or ★ Work row.
    ///
    /// An unset shortcut still draws its row — see the screen's note — with *"Not set"* where the
    /// address would be, so the `›` has something to sit beside.
    ///
    /// Not a `@ViewBuilder`, and the address is read through ``address(for:)`` rather than bound to
    /// a local: a `let` in the middle of a result builder is the kind of thing that compiles on one
    /// Swift version and not the next, which the C100 handoff already records once.
    private func shortcutRow(_ shortcut: AddressShortcut, showsSeparator: Bool) -> some View {
        Button {
            Task { await model.editShortcut(shortcut) }
        } label: {
            GroupedValueRow(
                title: (shortcut.titleKey ?? "").localised,
                subtitle: address(for: shortcut).map(\.oneLine) ?? "addresses_not_set".localised,
                symbolName: "star.fill",
                // The wireframe's own per-row tints: Home is `--primary`, Work is `--secondary`.
                symbolTint: shortcut == .home ? MageRideColor.primary : MageRideColor.secondary,
                showsSeparator: showsSeparator
            ) {
                RowChevron()
            }
        }
        .buttonStyle(.plain)
        .disabled(model.state.busyWith != nil && model.state.busyWith == address(for: shortcut)?.addressId)
    }

    private func address(for shortcut: AddressShortcut) -> SavedAddress? {
        shortcut == .home ? model.state.home : model.state.work
    }

    /// The wireframe's 100pt `.map` with a fixed pin over its centre.
    private var pinMap: some View {
        ZStack {
            MageRideMap(
                camera: model.state.pin.map { MapCamera(lat: $0.lat, lng: $0.lng) } ?? .colombo,
                onCameraIdle: model.onPinMoved
            )
            // Drawn over the map rather than as an annotation, which is what makes it stay exactly
            // at the centre through every gesture. See the model's note.
            Image(systemName: "mappin")
                .font(.system(size: MageRideControl.listRowIcon))
                .foregroundStyle(MageRideColor.pinPickup)
                .accessibilityHidden(true)
        }
        .frame(maxWidth: .infinity)
        .frame(height: MageRideControl.addressMapHeight)
        .clipShape(RoundedRectangle(cornerRadius: MageRideRadius.md, style: .continuous))
    }

    private var sheetBinding: Binding<Bool> {
        Binding(
            get: { model.state.sheet != nil },
            set: { presented in if !presented { model.dismissSheet() } }
        )
    }
}

/// SCR-PI-026a — *"Add address"*, the `.sheet` that captures one (AL-26, US-22.2).
///
/// **Four fields, and the fence says which four**: Address Line 1 (street/building), Address Line 2
/// (area/suburb), Address Line 3 (city/district) and a free-text **Label**. There is no Home/Work
/// control here and no language row — the shortcut was decided by the row that opened the sheet, and
/// SCR-PI-026 is where the pin was placed.
///
/// **The `Pinned:` caption is the honesty of the whole screen.** It prints the coordinate the row
/// will be saved at, because everything else on the sheet is text the passenger can edit: the lines
/// are a *label for a point*, and the point is what a booking will actually navigate to.
///
/// **Delete lives here** (US-22.3). SCR-PI-026 draws a `›` per row and no ✕; reaching delete through
/// the edit sheet also means the passenger has just been shown which address it is. Drawn as
/// destructive text under the CTA rather than as a second bar — one orange bar per screen is §0.2's
/// CTA token, and a full-width red one beside it reads as an equal choice.
struct AddAddressSheet: View {

    let sheet: AddressSheetState
    let onLabelChanged: (String) -> Void
    let onLine1Changed: (String) -> Void
    let onLine2Changed: (String) -> Void
    let onLine3Changed: (String) -> Void
    let onSave: () -> Void
    let onDelete: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: MageRideSpacing.sm) {
                Text(key: sheet.isEditing ? "address_sheet_edit_title" : "address_sheet_add_title")
                    .mageFont(.title)
                    .foregroundStyle(MageRideColor.onSurface)

                // The coordinate is a Swift-formatted constant inside translated copy — see
                // ``Coordinates``, which is why the digits are not in the three `.strings` files.
                Text("address_sheet_pinned".localisedFormat(sheet.pinned))
                    .mageFont(.caption)
                    .monospacedDigit()
                    .foregroundStyle(MageRideColor.onSurfaceVariant)

                if sheet.isLocating {
                    LoadingRow(messageKey: "address_sheet_locating")
                }

                LabelledTextField(labelKey: "address_line1", value: binding(sheet.line1, onLine1Changed))
                LabelledTextField(labelKey: "address_line2", value: binding(sheet.line2, onLine2Changed))
                LabelledTextField(labelKey: "address_line3", value: binding(sheet.line3, onLine3Changed))
                LabelledTextField(
                    labelKey: "address_label",
                    value: binding(sheet.label, onLabelChanged),
                    placeholder: "address_label_hint".localised
                )

                Button(action: onSave) {
                    Text(key: "address_save")
                }
                .buttonStyle(.mageCta(loading: sheet.isSaving))
                .disabled(!sheet.canSave)
                .padding(.top, MageRideSpacing.xxs)

                if sheet.isEditing {
                    Button(action: onDelete) {
                        Text(key: "address_delete")
                            .mageFont(.bodySmall)
                            .foregroundStyle(MageRideColor.error)
                            .frame(maxWidth: .infinity, minHeight: MageRideControl.minimumTapTarget)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .disabled(sheet.isSaving)
                }
            }
            .padding(.horizontal, MageRideSpacing.md)
            .padding(.top, MageRideSpacing.md)
            .padding(.bottom, MageRideSpacing.lg)
        }
        .background(MageRideColor.surface)
        .presentationDetents([.height(MageRideControl.addressSheetHeight), .large])
        .presentationDragIndicator(.visible)
    }

    /// The fields are edited through the model rather than bound to it, because the sheet is handed
    /// a **value**: a `@Binding` into `@Published private(set) var state` does not exist, and the
    /// alternative is the sheet owning four `@State`s that the reverse geocode could not fill in.
    private func binding(_ value: String, _ set: @escaping (String) -> Void) -> Binding<String> {
        Binding(get: { value }, set: set)
    }
}
