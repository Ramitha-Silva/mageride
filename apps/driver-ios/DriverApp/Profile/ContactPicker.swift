import Contacts
import ContactsUI
import SwiftUI

/// SCR-DI-029's address-book picker for the AL-13 emergency contact.
///
/// **No `NSContactsUsageDescription`, and no authorisation request — deliberately.**
/// `CNContactPickerViewController` runs **out of process**: the app never sees the address book, the
/// system does, and what comes back is the one row the driver tapped. Requesting `CNContactStore`
/// access would work too, and would ask a driver for their whole contact list in order to store one
/// number. This is the same trade `apps/driver-android`'s `ACTION_PICK` over `Phone.CONTENT_URI` makes
/// — its returned URI carries a one-shot read grant and needs no `READ_CONTACTS` — and it is the reason
/// neither app declares a contacts permission.
///
/// **Two delegate callbacks, because a contact with two numbers is a different tap from one with one.**
/// `predicateForSelectionOfContact` sends a single-number contact straight back on the first tap;
/// anything else drills into the number list and answers a `CNContactProperty`. Handling only the first
/// would make a driver's two-number contact silently do nothing.
///
/// - Parameters:
///   - onPicked: The chosen contact's display name and its number **exactly as the address book stores
///     it** — ``PhoneNumber/normalise(_:)`` is what turns `0771234567`, `+94 77 123 4567` and
///     `077-1234567` into the same nine digits. Not called at all when the driver backed out: a picker
///     that cannot answer leaves the two fields to be typed.
struct ContactPickerView: UIViewControllerRepresentable {

    let onPicked: (_ name: String, _ phone: String) -> Void
    let onDismiss: () -> Void

    func makeUIViewController(context: Context) -> CNContactPickerViewController {
        let picker = CNContactPickerViewController()
        picker.delegate = context.coordinator
        // Only the numbers, and only contacts that have one: an emergency contact with no number is a
        // row the driver can tap and cannot use.
        picker.displayedPropertyKeys = [CNContactPhoneNumbersKey]
        picker.predicateForEnablingContact = NSPredicate(format: "phoneNumbers.@count > 0")
        picker.predicateForSelectionOfContact = NSPredicate(format: "phoneNumbers.@count == 1")
        // Without this, tapping a number inside a multi-number contact performs the property's
        // **default action** — which for a phone number is placing a call. `true` makes the tap a
        // selection, which is the whole point of opening the picker.
        picker.predicateForSelectionOfProperty = NSPredicate(value: true)
        return picker
    }

    func updateUIViewController(_ controller: CNContactPickerViewController, context: Context) {
        // Nothing to push: the controller owns its own state, and re-assigning the delegate on each
        // SwiftUI update is how a picker in flight gets torn down.
    }

    func makeCoordinator() -> Coordinator { Coordinator(view: self) }

    /// The delegate. A class, because `CNContactPickerDelegate` is an Objective-C protocol and a
    /// SwiftUI `View` is a struct.
    final class Coordinator: NSObject, CNContactPickerDelegate {

        private let view: ContactPickerView

        init(view: ContactPickerView) {
            self.view = view
        }

        /// A contact with exactly one number, chosen in one tap.
        func contactPicker(_ picker: CNContactPickerViewController, didSelect contact: CNContact) {
            guard let number = contact.phoneNumbers.first?.value.stringValue else {
                view.onDismiss()
                return
            }
            view.onPicked(Self.name(of: contact), number)
        }

        /// One number of a contact that has several.
        func contactPicker(_ picker: CNContactPickerViewController, didSelect property: CNContactProperty) {
            guard let number = (property.value as? CNPhoneNumber)?.stringValue else {
                view.onDismiss()
                return
            }
            view.onPicked(Self.name(of: property.contact), number)
        }

        func contactPickerDidCancel(_ picker: CNContactPickerViewController) {
            view.onDismiss()
        }

        /// The name as the driver's own address book renders it.
        ///
        /// `CNContactFormatter` rather than `givenName + familyName`, because name order is locale data:
        /// a Sinhala or Tamil contact stored family-name-first is printed that way by the formatter and
        /// backwards by a concatenation. The fallback is the organisation name, which is what an
        /// address book holds for a contact filed under a business — and an empty one after that,
        /// because a nameless contact is a real row and the field the picker fills is editable.
        static func name(of contact: CNContact) -> String {
            CNContactFormatter.string(from: contact, style: .fullName)
                ?? contact.organizationName
        }
    }
}
