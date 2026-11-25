// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "BoBoAppleCore",
    platforms: [
        .macOS(.v13),
        .iOS(.v16)
    ],
    products: [
        .library(
            name: "BoBoAppleCore",
            targets: ["BoBoAppleCore"]
        )
    ],
    targets: [
        .target(
            name: "BoBoAppleCore",
            path: "Sources/BoBoAppleCore"
        ),
        .testTarget(
            name: "BoBoAppleCoreTests",
            dependencies: ["BoBoAppleCore"],
            path: "Tests/BoBoAppleCoreTests"
        )
    ]
)
