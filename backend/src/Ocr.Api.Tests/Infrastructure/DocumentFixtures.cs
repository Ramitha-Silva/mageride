using OpenCvSharp;

namespace MageRide.Ocr.Tests.Infrastructure;

/// <summary>
/// The documents this suite extracts from, drawn rather than checked in.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rendered fixture is a readable one.</b> The redaction assertions are about <em>which pixels
/// changed</em> — the region where the NIC was printed, the region where the portrait was — and a
/// checked-in JPEG makes every one of those a magic rectangle nobody can check against the document
/// in review. Here the text and its position are in the source, so "the NIC was masked" is asserted
/// against the coordinates the NIC was drawn at.
/// </para>
/// <para>
/// <b>They are also real enough for the real engine.</b> Hershey-simplex text at this size is read
/// by <c>tesseract --psm 11</c> at 90%+ per-word confidence, which is what lets the Tesseract
/// fallback test assert on fields rather than on a mock.
/// </para>
/// </remarks>
internal static class DocumentFixtures
{
    /// <summary>Where the portrait is drawn on the licence, so a face-blur assertion has a region.</summary>
    public static readonly Rect PortraitRegion = new(40, 150, 140, 170);

    /// <summary>Where the NIC is printed, so an ID-mask assertion has a region.</summary>
    public static readonly Rect NicRegion = new(230, 250, 420, 40);

    public const string LicenceNumber = "B1234567";
    public const string NicNumber = "199012345678";
    public const string LicenceExpiry = "2029-04-30";
    public const string InsuranceExpiry = "2027-03-31";
    public const string RevenueNumber = "RL8891234";
    public const string RevenueExpiry = "2027-01-31";
    public const string PermitNumber = "NTC554321";
    public const string Plate = "WP-QA-1234";

    /// <summary>The front of a driving licence: a portrait, a licence number, a NIC and an expiry.</summary>
    public static byte[] DrivingLicenceFront() => Render(720, 400, image =>
    {
        // A filled ellipse where the portrait belongs. It is not a face and the Haar cascade will
        // not fire on it — which is exactly why the face-blur tests drive the detector through the
        // port instead of hoping a drawing fools a trained classifier.
        Cv2.Ellipse(
            image,
            new Point(PortraitRegion.X + (PortraitRegion.Width / 2), PortraitRegion.Y + (PortraitRegion.Height / 2)),
            new Size(PortraitRegion.Width / 2, PortraitRegion.Height / 2),
            0, 0, 360, new Scalar(90, 110, 130), -1);

        Text(image, "DRIVING LICENCE", new Point(40, 60), 0.9);
        Text(image, "LICENCE NO " + LicenceNumber, new Point(230, 170), 0.8);
        Text(image, "DATE OF EXPIRY " + "30.04.2029", new Point(230, 215), 0.8);
        Text(image, "NIC " + NicNumber, new Point(NicRegion.X, NicRegion.Y + 30), 0.8);
    });

    /// <summary>The reverse: the table of licence classes (AL-29).</summary>
    public static byte[] DrivingLicenceBack() => Render(720, 300, image =>
    {
        Text(image, "CLASSES", new Point(40, 60), 0.9);
        Text(image, "A1 B C1", new Point(40, 140), 1.1);
    });

    /// <summary>An insurance certificate. D5' §14.1a verifies it on the expiry alone.</summary>
    public static byte[] InsuranceCertificate() => Render(720, 320, image =>
    {
        Text(image, "MOTOR INSURANCE", new Point(40, 60), 0.9);
        Text(image, "INSURER CEYLINCO", new Point(40, 130), 0.8);
        Text(image, "POLICY 4477112", new Point(40, 190), 0.8);
        Text(image, "EXPIRY 31.03.2027", new Point(40, 250), 0.8);
    });

    /// <summary>A revenue licence. D5' §14.1a needs the number AND the expiry.</summary>
    public static byte[] RevenueLicence() => Render(720, 300, image =>
    {
        Text(image, "REVENUE LICENCE", new Point(40, 60), 0.9);
        Text(image, "LICENCE NO " + RevenueNumber, new Point(40, 140), 0.8);
        Text(image, "EXPIRY 31.01.2027", new Point(40, 210), 0.8);
    });

    /// <summary>A route permit (AL-50, Fleet Portal).</summary>
    public static byte[] RoutePermit() => Render(720, 320, image =>
    {
        Text(image, "ROUTE PERMIT", new Point(40, 60), 0.9);
        Text(image, "PERMIT NO " + PermitNumber, new Point(40, 130), 0.8);
        Text(image, "ROUTE 138 MAHARAGAMA", new Point(40, 190), 0.8);
        Text(image, "EXPIRY 31.12.2027", new Point(40, 250), 0.8);
    });

    /// <summary>A vehicle photograph: a plate on a body panel. Step 4/4's whole content.</summary>
    public static byte[] VehiclePhoto(string plate = Plate) => Render(720, 320, image =>
    {
        Cv2.Rectangle(image, new Rect(140, 110, 440, 110), new Scalar(30, 30, 30), 3);
        Text(image, plate, new Point(165, 185), 1.6, 3);
    });

    /// <summary>Bytes that are not an image at all.</summary>
    public static byte[] NotAnImage() => System.Text.Encoding.UTF8.GetBytes("this is not a document");

    private static void Text(Mat image, string value, Point origin, double scale, int thickness = 2) =>
        Cv2.PutText(image, value, origin, HersheyFonts.HersheySimplex, scale, Scalar.Black, thickness);

    private static byte[] Render(int width, int height, Action<Mat> draw)
    {
        using var image = new Mat(height, width, MatType.CV_8UC3, Scalar.All(245));

        draw(image);

        // PNG: the raw upload has to survive the round trip byte-for-byte so its sha256 is a stable
        // thing to assert the redacted copy is NOT.
        Cv2.ImEncode(".png", image, out var encoded);

        return encoded;
    }
}
