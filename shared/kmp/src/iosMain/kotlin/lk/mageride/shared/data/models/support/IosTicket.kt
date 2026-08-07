package lk.mageride.shared.data.models.support

/**
 * What the user wrote on [ticket], read from Swift (C093).
 *
 * **A wrapper because `description` is `NSObject`'s.** Every Kotlin class reaches Objective-C as a
 * subclass of `KotlinBase`, which is an `NSObject`, and `NSObject` already declares
 * `- (NSString *)description`. `TicketDetail.description` therefore **collides** with an inherited
 * selector, and the exporter resolves the collision by mangling the Kotlin name — so the property a
 * Swift call site would reach for by that name is either not there or is `NSObject`'s own
 * `CustomStringConvertible` output, which is a debug string and not a driver's complaint.
 *
 * Neither outcome is one a screen should be discovering: the first is a build failure on a host that
 * cannot build, the second is SCR-DI-033's thread sheet printing `TicketDetail(ticketId=…)` at a
 * driver. A one-line function with an unambiguous name removes the question, and it is the same
 * reasoning behind every other `iosMain` helper in this module — the bridge carries values, and
 * whichever side can name the operation honestly owns it.
 *
 * Nothing else on the support surface has this problem: `FaqArticle.body` and `TicketEvent.body` are
 * ordinary names, and `CreateSupportTicketRequest`'s `description` is a **constructor parameter**,
 * which becomes part of an `initWith…:` selector and collides with nothing.
 *
 * @param ticket The ticket `GET /v1/support/tickets/{userId}/{ticketId}` answered.
 */
public fun ticketDescription(ticket: TicketDetail): String = ticket.description
