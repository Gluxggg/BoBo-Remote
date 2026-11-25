// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "BoBoRemoteMac",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(name: "BoBoRemoteMac", targets: ["BoBoRemoteMac"])
    ],
    dependencies: [
        .package(path: "../BoBoAppleCore")
    ],
    targets: [
        .executableTarget(
            name: "BoBoRemoteMac",
            dependencies: [
                .product(name: "BoBoAppleCore", package: "BoBoAppleCore")
            ],
            path: "Sources/BoBoRemoteMac"
        )
    ]
)
